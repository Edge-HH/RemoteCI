using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 文件分发接收端：把命令携带的文件字节写入固定的用户文件夹。
/// 只接受净化后的文件名与 FileTargetFolder 枚举内的目标目录，不接收任意路径；
/// 默认不覆盖已有文件，而是自动追加序号。
/// </summary>
public sealed class FileReceiveService
{
    /// <summary>Windows 已知文件夹“下载”的 GUID，用于从注册表解析用户自定义的下载位置。</summary>
    private const string DownloadsFolderGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

    private readonly ILogger _logger;

    public FileReceiveService(ILogger<FileReceiveService>? logger = null) =>
        _logger = logger ?? NullLogger<FileReceiveService>.Instance;

    public async Task<CommandResult> SaveAsync(FileDistributionRequest? request)
    {
        if (!FileDistributionRequest.TryDecode(request, out var content))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "文件内容无效或超过 10 MB");
        var fileName = FileDistributionRequest.SanitizeFileName(request!.FileName);
        if (fileName is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "文件名无效");
        var folder = ResolveFolder(request.TargetFolder);
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
