using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 把插件包放进服务端文件中转，供 InstallPluginPackages 命令分发：
/// “服务端预下载”由服务端从插件市场下载并校验 MD5；“从 CIPX 安装”由管理员上传。
/// 两者都核对包内 manifest.yml，之后设备凭下载凭据从中转分段下载并校验 SHA-256。
/// </summary>
public sealed class PluginPackageService(
    FileTransferService transfers,
    IHttpClientFactory httpClients,
    ILogger<PluginPackageService> logger)
{
    /// <summary>单个插件包上限；ClassIsland 插件包通常只有几 MB。</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    // 同一版本的市场插件包在中转保留期内复用，避免每次预下载都重新访问市场。
    private readonly ConcurrentDictionary<string, string> _marketCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    /// <summary>服务端预下载：逐个下载市场插件包（镜像依次回退）并放入中转。</summary>
    public async Task<IReadOnlyList<PreparedPluginPackage>> PrepareFromMarketAsync(
        IReadOnlyList<(MarketPlugin Plugin, bool IsDependency)> plugins, UserProfile owner, CancellationToken ct = default)
    {
        var prepared = new List<PreparedPluginPackage>();
        await _downloadLock.WaitAsync(ct);
        try
        {
            foreach (var (plugin, isDependency) in plugins)
                prepared.Add(await PrepareOneAsync(plugin, isDependency, owner, ct));
        }
        finally
        {
            _downloadLock.Release();
        }
        return prepared;
    }

    private async Task<PreparedPluginPackage> PrepareOneAsync(
        MarketPlugin plugin, bool isDependency, UserProfile owner, CancellationToken ct)
    {
        var key = $"{plugin.Id}|{plugin.Version}|{plugin.DownloadMd5}";
        if (_marketCache.TryGetValue(key, out var cachedId) && transfers.Get(cachedId) is { } cached &&
            ReadManifest(cached) is { } cachedManifest)
            return new PreparedPluginPackage(cached, cachedManifest, isDependency);

        var errors = new List<string>();
        foreach (var url in plugin.DownloadUrls)
        {
            var temp = Path.Combine(transfers.Directory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await DownloadAsync(url, temp, plugin.DownloadMd5, ct);
                PluginPackageManifest? manifest;
                await using (var check = File.OpenRead(temp)) manifest = PluginPackageManifest.TryRead(check);
                if (manifest is null || !string.Equals(manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件包清单与市场信息不一致");

                FileTransferRecord record;
                await using (var content = File.OpenRead(temp))
                    record = await transfers.CreateAsync(content, $"{plugin.Id}.cipx", owner, ct);
                _marketCache[key] = record.Id;
                logger.LogInformation("已预下载插件包 {Id} {Version}：{Url}", plugin.Id, plugin.Version, url);
                return new PreparedPluginPackage(record, manifest, isDependency);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                           && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "预下载插件包失败：{Url}", url);
                errors.Add(ex.Message);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        throw new PluginMarketException($"服务端下载插件 {plugin.Name}（{plugin.Id}）失败：{errors.LastOrDefault() ?? "没有可用的下载地址"}");
    }

    private async Task DownloadAsync(string url, string path, string expectedMd5, CancellationToken ct)
    {
        using var response = await httpClients.CreateClient(PluginMarketCatalog.HttpClientName)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPackageBytes)
            throw new InvalidDataException("插件包超过大小上限");
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes) throw new InvalidDataException("插件包超过大小上限");
                md5.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        var actual = Convert.ToHexString(md5.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(expectedMd5) && !string.Equals(actual, expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件包 MD5 校验失败");
    }

    /// <summary>管理员上传 .cipx：落入中转后核对清单，无效时立即删除。</summary>
    public async Task<PreparedPluginPackage> UploadAsync(
        Stream body, string? fileName, UserProfile owner, CancellationToken ct = default)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".cipx", StringComparison.OrdinalIgnoreCase))
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, "请选择 .cipx 插件包");
        var record = await transfers.CreateAsync(body, fileName, owner, ct);
        if (record.Size > MaxPackageBytes || ReadManifest(record) is not { } manifest || !PluginPackageManifest.IsValidId(manifest.Id))
        {
            transfers.Delete(record.Id);
            throw new FileTransferException(ApiErrorCodes.InvalidRequest,
                record.Size > MaxPackageBytes ? "插件包超过 512 MB" : "不是有效的 ClassIsland 插件包（缺少 manifest.yml 或插件 Id 无效）");
        }
        return new PreparedPluginPackage(record, manifest, false);
    }

    /// <summary>按传输编号找回已上传的插件包；只有上传者本人或系统管理员可以使用。</summary>
    public PreparedPluginPackage? FindUploaded(string? transferId, UserProfile user) =>
        transfers.Get(transferId) is { } record && FileTransferService.CanManage(record, user) &&
        ReadManifest(record) is { } manifest && PluginPackageManifest.IsValidId(manifest.Id)
            ? new PreparedPluginPackage(record, manifest, false)
            : null;

    /// <summary>为一台目标设备登记下载权限，并生成命令中的插件包引用。</summary>
    public PluginPackageRef CreateRef(PreparedPluginPackage package, DeviceInventory target)
    {
        transfers.AddDelivery(package.Record, new FileTransferDelivery
        {
            CredentialId = target.CredentialId,
            ClassId = target.ClassId,
            TargetName = target.Assigned ? target.ClassName : target.DeviceName,
            State = FileTransferState.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return new PluginPackageRef
        {
            PluginId = package.Manifest.Id,
            Name = package.Manifest.Name,
            Version = package.Manifest.Version,
            IsDependency = package.IsDependency,
            TransferId = package.Record.Id,
            DownloadKey = package.Record.DownloadKey,
            Size = package.Record.Size,
            Sha256 = package.Record.Sha256,
        };
    }

    private PluginPackageManifest? ReadManifest(FileTransferRecord record)
    {
        try
        {
            return PluginPackageManifest.TryRead(transfers.ContentPath(record.Id));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed record PreparedPluginPackage(FileTransferRecord Record, PluginPackageManifest Manifest, bool IsDependency);
