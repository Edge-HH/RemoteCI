using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 教室端安装 ExamAware、放入考试插件，并把考试档案写到 ExamAware 插件约定的目录。
/// 命令立即回执，下载和安装在后台完成。
/// </summary>
public sealed class ExamClassroomService
{
    private readonly FileReceiveService _files;
    private readonly ILogger<ExamClassroomService> _logger;
    private readonly string _root;
    private int _installing;
    private int _distributing;

    public ExamClassroomService(FileReceiveService files, ILogger<ExamClassroomService> logger)
    {
        _files = files;
        _logger = logger;
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCI");
    }

    public static string PluginsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExamAware", "plugins");

    public CommandResult StartInstall(ExamInstallRequest? request)
    {
        if (ValidateInstall(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        if (_files.Channel is null)
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未连接云端，无法下载安装包");
        if (Interlocked.Exchange(ref _installing, 1) == 1)
            return CommandResult.Failure(CommandResultCodes.Busy, "已有 ExamAware 安装任务正在执行");
        _ = Task.Run(() => RunInstallAsync(request!));
        var plugin = request!.InstallPlugin ? "，并安装考试插件" : "";
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = $"开始安装 ExamAware{plugin}",
        };
    }

    public CommandResult StartDistribute(ExamDistributeRequest? request)
    {
        if (ValidateDistribute(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        if (_files.Channel is null)
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未连接云端，无法下载考试档案");
        if (Interlocked.Exchange(ref _distributing, 1) == 1)
            return CommandResult.Failure(CommandResultCodes.Busy, "已有考试正在接收");
        _ = Task.Run(() => RunDistributeAsync(request!));
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = $"开始接收考试档案 {request!.Exam!.FileName}",
        };
    }

    private async Task RunInstallAsync(ExamInstallRequest request)
    {
        var installerFile = request.Installer!;
        var pluginFile = request.Plugin;
        try
        {
            var work = Path.Combine(_root, "install");
            Directory.CreateDirectory(work);
            var installer = await DownloadAsync(installerFile, work, ".exe");
            if (installer is null) return;
            if (request.InstallPlugin && pluginFile is not null)
            {
                var package = await DownloadAsync(pluginFile, work, ".ea2x");
                if (package is null) return;
                try
                {
                    var installed = ExamPluginPackage.Extract(package, PluginsDirectory);
                    _logger.LogInformation("已安装 ExamAware 插件：{Path}", installed);
                    await _files.ReportFinalAsync(pluginFile.ToTransferRequest(), true, $"插件已安装到 {installed}");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "安装 ExamAware 插件失败");
                    await _files.ReportFinalAsync(pluginFile.ToTransferRequest(), false, ex.Message);
                    return;
                }
                finally
                {
                    TryDelete(package);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                await _files.ReportFinalAsync(installerFile.ToTransferRequest(), false, "只能在 Windows 教室电脑上安装 ExamAware");
                return;
            }

            var exit = RunInstaller(installer);
            TryDelete(installer);
            if (exit is null)
            {
                await _files.ReportFinalAsync(installerFile.ToTransferRequest(), false, "未能启动安装程序");
                return;
            }
            if (exit != 0)
            {
                await _files.ReportFinalAsync(installerFile.ToTransferRequest(), false, $"安装程序退出码 {exit}");
                return;
            }
            await _files.ReportFinalAsync(installerFile.ToTransferRequest(), true, "ExamAware 已安装");
            if (request.LaunchAfter) LaunchExamAware();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 ExamAware 失败");
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    private async Task RunDistributeAsync(ExamDistributeRequest request)
    {
        try
        {
            var work = Path.Combine(_root, "install");
            Directory.CreateDirectory(work);
            var staged = await DownloadAsync(request.Exam!, work, ".ea2");
            if (staged is null) return;
            var destination = Path.Combine(_root, "Exams", request.LocalFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(staged, destination, true);
            await _files.ReportFinalAsync(request.Exam!.ToTransferRequest(), true, $"考试档案已保存到 {destination}");
            _logger.LogInformation("考试档案已保存：{Path}", destination);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "接收考试档案失败");
            await _files.ReportFinalAsync(request.Exam!.ToTransferRequest(), false, "保存考试档案失败");
        }
        finally
        {
            Interlocked.Exchange(ref _distributing, 0);
        }
    }

    private async Task<string?> DownloadAsync(ExamFileRef file, string work, string extension)
    {
        var staged = await _files.ReceiveToStagingAsync(file.ToTransferRequest(), CancellationToken.None);
        if (staged is null) return null;
        var target = Path.Combine(work, file.TransferId + extension);
        File.Move(staged, target, true);
        return target;
    }

    private int? RunInstaller(string installerPath)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/S",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            };
            if (OperatingSystem.IsWindows() && !IsAdministrator()) info.Verb = "runas";
            using var process = Process.Start(info);
            if (process is null) return null;
            if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { _logger.LogDebug(ex, "结束安装程序失败"); }
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 ExamAware 安装程序失败");
            return null;
        }
    }

    private void LaunchExamAware()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "ExamAware", "ExamAware.exe");
            if (!File.Exists(exe)) continue;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动 ExamAware 失败：{Path}", exe);
            }
        }
        _logger.LogWarning("未找到 ExamAware.exe，安装完成后需要手动打开");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? ValidateInstall(ExamInstallRequest? request)
    {
        if (request?.Installer is null) return "缺少 ExamAware 安装程序";
        if (request.Installer.Validate() is { } error) return error;
        if (!request.Installer.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "安装程序必须是 .exe";
        if (!request.InstallPlugin) return null;
        if (request.Plugin is null) return "缺少 ExamAware 插件包";
        if (request.Plugin.Validate() is { } pluginError) return pluginError;
        return request.Plugin.FileName.EndsWith(".ea2x", StringComparison.OrdinalIgnoreCase) ||
               request.Plugin.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? null
            : "插件包必须是 .ea2x 或 .zip";
    }

    private static string? ValidateDistribute(ExamDistributeRequest? request)
    {
        if (request?.Exam is null || request.JobId == Guid.Empty) return "缺少考试档案";
        if (request.Exam.Validate() is { } error) return error;
        if (!request.Exam.FileName.EndsWith(".ea2", StringComparison.OrdinalIgnoreCase))
            return "考试档案必须是 .ea2";
        if (!string.Equals(request.LocalFileName, ExamDistributeRequest.FileNameFor(request.JobId), StringComparison.OrdinalIgnoreCase))
            return "考试档案保存名无效";
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>把 .ea2x / .zip 插件包解压到 ExamAware 的用户插件目录，拒绝跳出目录的路径。</summary>
internal static class ExamPluginPackage
{
    public static string Extract(string zipPath, string pluginsRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifest = archive.GetEntry("package.json")
            ?? throw new InvalidDataException("插件包根目录缺少 package.json");
        using var reader = new StreamReader(manifest.Open());
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        if (!doc.RootElement.TryGetProperty("examaware", out var meta) || meta.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("不是 ExamAware 插件包");
        if (!doc.RootElement.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { Length: > 0 } name)
            throw new InvalidDataException("插件包缺少名称");
        var folder = FolderName(name);
        var target = Path.Combine(pluginsRoot, folder);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target + Path.DirectorySeparatorChar);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException("插件包路径无效");
            var destination = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件包路径无效");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
        return target;
    }

    public static string FolderName(string packageName)
    {
        var name = packageName.Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("插件名无效");
        return name;
    }
}
