using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>插件访问服务端文件中转的通道，由云端客户端实现；测试可替换。</summary>
public interface IFileTransferChannel
{
    /// <summary>从 offset 开始请求文件内容（offset &gt; 0 时带 Range 头）；调用方负责释放响应。</summary>
    Task<HttpResponseMessage> RequestFileAsync(string transferId, string downloadKey, long offset, CancellationToken ct);

    /// <summary>上报接收进度；连接不可用时返回 false。</summary>
    Task<bool> ReportFileProgressAsync(FileTransferProgress progress);
}

/// <summary>
/// 文件分发接收端：把文件写入固定的用户文件夹。
/// 只接受净化后的文件名与 FileTargetFolder 枚举内的目标目录，不接收任意路径；
/// 默认不覆盖已有文件，而是自动追加序号。
/// 中转方式（file.transfer）收到命令后立即回执，在后台分段下载：中断后按已下载长度续传、
/// 完成后校验 SHA-256，再从暂存目录移动到目标文件夹，过程中按时间节流上报进度。
/// </summary>
public sealed class FileReceiveService
{
    /// <summary>Windows 已知文件夹“下载”的 GUID，用于从注册表解析用户自定义的下载位置。</summary>
    private const string DownloadsFolderGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary>网络中断后的最大重试次数（每次从已下载位置续传）。</summary>
    internal const int MaxAttempts = 8;

    private readonly ILogger _logger;
    private readonly Func<FileTargetFolder, string?> _resolveFolder;
    private readonly string _stagingDirectory;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloadSlots = new(2, 2);

    public FileReceiveService(ILogger<FileReceiveService>? logger = null)
        : this(logger, null, null)
    {
    }

    internal FileReceiveService(
        ILogger<FileReceiveService>? logger,
        Func<FileTargetFolder, string?>? folderResolver,
        string? stagingDirectory)
    {
        _logger = logger ?? NullLogger<FileReceiveService>.Instance;
        _resolveFolder = folderResolver ?? ResolveFolder;
        _stagingDirectory = stagingDirectory ?? Path.Combine(Path.GetTempPath(), "RemoteCI-Transfers");
    }

    /// <summary>云端连接建立后注入；为 null 时中转方式的命令直接失败。</summary>
    public IFileTransferChannel? Channel { get; set; }

    /// <summary>最小进度上报间隔；测试可缩短。</summary>
    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>单次读取无数据的最长等待，超过视为连接卡死并续传重试。</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>重试前的等待；测试可置零。</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } =
        attempt => TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(attempt, 4)));

    /// <summary>最终状态上报失败时的重发间隔；测试可置零。</summary>
    internal TimeSpan FinalReportRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>最近一次启动的后台下载任务，供测试等待完成。</summary>
    internal Task? LastTransfer { get; private set; }

    /// <summary>插件停止时取消全部进行中的下载；暂存的半成品保留，下次同一传输可以续传。</summary>
    public void CancelAll()
    {
        foreach (var cts in _active.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task<CommandResult> SaveAsync(FileDistributionRequest? request)
    {
        if (request?.IsTransfer == true) return StartTransfer(request);
        if (!FileDistributionRequest.TryDecode(request, out var content))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "文件内容无效或超过 10 MB");
        var fileName = FileDistributionRequest.SanitizeFileName(request!.FileName);
        if (fileName is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "文件名无效");
        var folder = _resolveFolder(request.TargetFolder);
        if (folder is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "目标文件夹不可用");

        var target = ResolveTargetPath(folder, fileName, request.Overwrite);
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(target, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "文件分发写入失败：{Path}", target);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, "文件写入失败，请检查设备磁盘与权限");
        }

        var renamed = !string.Equals(
            Path.Combine(folder, fileName),
            target,
            StringComparison.OrdinalIgnoreCase);
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = renamed ? $"已保存到 {target}（自动重命名以避免覆盖）" : $"已保存到 {target}",
            Data = target,
        };
    }

    private CommandResult StartTransfer(FileDistributionRequest request)
    {
        if (FileDistributionRequest.ValidateTransfer(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        var fileName = FileDistributionRequest.SanitizeFileName(request.FileName)!;
        var folder = _resolveFolder(request.TargetFolder);
        if (folder is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "目标文件夹不可用");
        if (Channel is not { } channel)
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未连接云端，无法下载文件");
        if (FreeSpace(_stagingDirectory) is { } free && free < request.Size)
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"设备磁盘空间不足（需要 {FormatSize(request.Size)}）");

        var transferId = request.TransferId!;
        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(transferId, cts))
        {
            cts.Dispose();
            return new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "该文件正在接收中" };
        }
        LastTransfer = Task.Run(async () =>
        {
            try { await RunTransferAsync(channel, request, fileName, folder, cts.Token); }
            finally
            {
                _active.TryRemove(transferId, out _);
                cts.Dispose();
            }
        });
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = $"开始接收 {fileName}（{FormatSize(request.Size)}）",
        };
    }

    private async Task RunTransferAsync(
        IFileTransferChannel channel, FileDistributionRequest request, string fileName, string folder, CancellationToken ct)
    {
        var transferId = request.TransferId!;
        var total = request.Size;
        var partPath = Path.Combine(_stagingDirectory, transferId + ".part");
        var lastReport = DateTime.MinValue;

        async Task Report(FileTransferState state, long received, string? message = null, string? savedPath = null, bool force = false)
        {
            if (!force && DateTime.UtcNow - lastReport < ProgressInterval) return;
            lastReport = DateTime.UtcNow;
            var progress = new FileTransferProgress
            {
                TransferId = transferId,
                TotalBytes = total,
                State = state,
                ReceivedBytes = received,
                Message = message,
                SavedPath = savedPath,
            };
            var final = state is FileTransferState.Completed or FileTransferState.Failed;
            // 最终状态必须送达：云端短暂断线时稍后重发，最多等待约 2 分钟。
            for (var i = 0; i < (final ? 24 : 1); i++)
            {
                try
                {
                    if (await channel.ReportFileProgressAsync(progress)) return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "上报文件接收进度失败");
                }
                if (final) await Task.Delay(FinalReportRetryDelay, CancellationToken.None);
            }
        }

        try
        {
            await _downloadSlots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Report(FileTransferState.Failed, 0, "插件已停止，接收中断", force: true);
            return;
        }
        try
        {
            Directory.CreateDirectory(_stagingDirectory);
            await Report(FileTransferState.Downloading, PartLength(partPath), force: true);
            string? failure = null;
            for (var attempt = 0; ; attempt++)
            {
                var offset = PartLength(partPath);
                if (offset > total)
                {
                    File.Delete(partPath);
                    offset = 0;
                }
                if (offset == total) break;
                try
                {
                    failure = await DownloadOnceAsync(channel, request, partPath, offset,
                        received => Report(FileTransferState.Downloading, received), ct);
                    if (failure is not null) break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException ||
                                           (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    if (attempt + 1 >= MaxAttempts)
                    {
                        failure = "网络多次中断，下载失败";
                        break;
                    }
                    _logger.LogWarning(ex, "文件下载中断，第 {Attempt} 次重试：{Name}", attempt + 1, fileName);
                    await Report(FileTransferState.Downloading, PartLength(partPath), "网络中断，正在续传", force: true);
                    await Task.Delay(RetryDelay(attempt), ct);
                }
            }

            if (failure is null && !await HashMatchesAsync(partPath, request.Sha256!, ct))
            {
                TryDelete(partPath);
                failure = "文件校验失败（SHA-256 不一致），已丢弃";
            }
            if (failure is not null)
            {
                await Report(FileTransferState.Failed, PartLength(partPath), failure, force: true);
                return;
            }

            string target;
            try
            {
                Directory.CreateDirectory(folder);
                target = ResolveTargetPath(folder, fileName, request.Overwrite);
                File.Move(partPath, target, request.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogError(ex, "文件分发写入失败：{Name}", fileName);
                await Report(FileTransferState.Failed, total, "文件写入失败，请检查设备磁盘与权限", force: true);
                return;
            }
            var renamed = !string.Equals(Path.Combine(folder, fileName), target, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("文件分发已接收：{Path}", target);
            await Report(FileTransferState.Completed, total,
                renamed ? $"已保存到 {target}（自动重命名以避免覆盖）" : $"已保存到 {target}", target, force: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Report(FileTransferState.Failed, PartLength(partPath), "插件已停止，接收中断", force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件分发接收异常：{Name}", fileName);
            await Report(FileTransferState.Failed, PartLength(partPath), "接收异常，请查看 ClassIsland 日志", force: true);
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    /// <summary>一次 HTTP 下载；返回 null 表示本次已写完，返回文本表示不可重试的失败，网络异常向上抛出以便续传。</summary>
    private async Task<string?> DownloadOnceAsync(
        IFileTransferChannel channel, FileDistributionRequest request, string partPath, long offset,
        Func<long, Task> onProgress, CancellationToken ct)
    {
        using var response = await channel.RequestFileAsync(request.TransferId!, request.DownloadKey!, offset, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return "服务端文件已过期或无权下载";
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                // 暂存文件与服务端不一致，丢弃后从头下载。
                TryDelete(partPath);
                throw new IOException("续传位置无效");
            case HttpStatusCode.OK:
                // 服务端未按 Range 返回时从头写入。
                offset = 0;
                break;
            case HttpStatusCode.PartialContent:
                break;
            default:
                if ((int)response.StatusCode >= 500) throw new HttpRequestException($"服务端返回 {(int)response.StatusCode}");
                return $"下载失败（HTTP {(int)response.StatusCode}）";
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partPath, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        var received = offset;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            if (received + read > request.Size) return "下载的文件大小与预期不符";
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            await onProgress(received);
        }
        await output.FlushAsync(ct);
        if (received < request.Size) throw new IOException("连接提前结束");
        return null;
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static long PartLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long? FreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>解析目标用户文件夹；路径跟随系统的已知文件夹设置（含 OneDrive 重定向）。</summary>
    private static string? ResolveFolder(FileTargetFolder target) => target switch
    {
        FileTargetFolder.Desktop => NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        FileTargetFolder.Documents => NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        FileTargetFolder.Downloads => ResolveDownloadsFolder(),
        _ => null,
    };

    private static string? NonEmpty(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : path;

    private static string? ResolveDownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = TryReadDownloadsFromRegistry();
            if (fromRegistry is not null) return fromRegistry;
        }
        // 注册表不可用时退回到用户目录下的 Downloads；目录不存在时由写入方创建。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, "Downloads");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryReadDownloadsFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(DownloadsFolderGuid) is not string raw || string.IsNullOrWhiteSpace(raw))
                return null;
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            return Directory.Exists(expanded) ? expanded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string ResolveTargetPath(string folder, string fileName, bool overwrite)
    {
        var candidate = Path.Combine(folder, fileName);
        if (overwrite || !File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
