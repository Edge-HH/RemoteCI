using System.Diagnostics;
using System.Text.Json;

namespace RemoteCI.Server.Services;

/// <summary>由独立进程执行的更新计划；服务端退出后再替换 Windows 锁定文件。</summary>
public sealed class UpdateInstallPlan
{
    public required string SourceDirectory { get; init; }
    public required string DestinationDirectory { get; init; }
    public required string DotnetHostPath { get; init; }
    public required string ServerAssemblyPath { get; init; }
    public required string LogPath { get; init; }
    public int ServerProcessId { get; init; }
    public string[] ServerArguments { get; init; } = [];

    /// <summary>新服务端启动成功后写入的标记文件；为空则跳过健康检查（旧版兼容）。</summary>
    public string? StartupMarkerPath { get; init; }
}

/// <summary>
/// RemoteCI.Server.dll 的轻量更新入口。通过 <c>--apply-update</c> 启动时不会创建 Web 主机，
/// 只等待旧服务端退出、覆盖文件并重新拉起服务端。
/// </summary>
public static class UpdateInstaller
{
    public const string Command = "--apply-update";

    /// <summary>新服务端通过该环境变量获知启动成功标记路径。</summary>
    public const string StartupMarkerEnvVar = "REMOTECI_STARTUP_MARKER";
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartupHealthCheckTimeout = TimeSpan.FromSeconds(30);
    private const int CopyRetryCount = 120;

    /// <summary>更新包自带的是默认配置，直接覆盖会丢失部署目录中用户自定义的数据库路径、监听地址等。</summary>
    private static readonly string[] NeverOverwriteFiles =
        ["appsettings.json", "appsettings.Development.json", "appsettings.Production.json", "web.config"];

    public static async Task<bool> TryRunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length != 2 || !string.Equals(args[0], Command, StringComparison.Ordinal)) return false;

        UpdateInstallPlan? plan = null;
        try
        {
            await using var stream = File.OpenRead(args[1]);
            plan = await JsonSerializer.DeserializeAsync<UpdateInstallPlan>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("更新计划为空。");
            await ExecuteAsync(plan, ct);
        }
        catch (Exception ex)
        {
            if (plan is not null) AppendLog(plan.LogPath, $"更新失败：{ex}");
            else Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        return true;
    }

    public static async Task ExecuteAsync(UpdateInstallPlan plan, CancellationToken ct = default)
    {
        Validate(plan);
        AppendLog(plan.LogPath, $"等待服务端进程 {plan.ServerProcessId} 退出。");
        await WaitForExitAsync(plan.ServerProcessId, ct);

        string? backupRoot = null;
        IReadOnlyList<(string Relative, bool Existed)> journal = [];
        try
        {
            (backupRoot, journal) = await ApplyFilesWithRollbackAsync(
                plan.SourceDirectory, plan.DestinationDirectory, ct, CopyRetryCount);
            AppendLog(plan.LogPath, "更新文件替换完成。");
        }
        catch (Exception ex)
        {
            // ApplyFilesWithRollbackAsync 内部已尽力回滚到旧版本；无论成败都尝试重启，避免服务永久下线。
            AppendLog(plan.LogPath, $"文件替换失败，已回滚到旧版本后继续尝试启动：{ex.Message}");
        }

        var process = StartServer(plan, setStartupMarker: true)
            ?? throw new InvalidOperationException("更新完成，但无法重新启动 RemoteCI 服务端。");
        AppendLog(plan.LogPath, $"服务端已重新启动，进程 {process.Id}。");

        if (plan.StartupMarkerPath is { Length: > 0 } marker)
        {
            if (await WaitForStartupMarkerAsync(marker, process, ct))
            {
                AppendLog(plan.LogPath, "新版本启动健康检查通过。");
                TryDeleteBackup(backupRoot);
                TryDeleteStaging(plan);
            }
            else
            {
                // 新版本启动失败：终止进程、恢复旧文件并重启旧版本。
                AppendLog(plan.LogPath, "新版本启动健康检查失败，回滚到旧版本。");
                TryKill(process);
                if (backupRoot is not null) RestoreBackups(backupRoot, plan.DestinationDirectory, journal);
                var rollback = StartServer(plan, setStartupMarker: false);
                AppendLog(plan.LogPath, rollback is null
                    ? "旧版本回滚完成，但无法重新启动旧服务端，请人工处理。"
                    : $"旧版本已恢复并重新启动，进程 {rollback.Id}。");
                Environment.ExitCode = 1;
            }
        }
        else
        {
            TryDeleteBackup(backupRoot);
            TryDeleteStaging(plan);
        }
    }

    /// <summary>
    /// 更新成功后尽力清理暂存目录（更新源目录的父目录，内含计划文件、更新包与日志）。
    /// 文件锁等原因失败时保留现场，不影响已完成的更新；此后日志写入会静默失败，属预期行为。
    /// </summary>
    private static void TryDeleteStaging(UpdateInstallPlan plan)
    {
        var staging = Path.GetDirectoryName(Path.GetFullPath(plan.SourceDirectory));
        if (staging is null || Path.GetPathRoot(staging) == staging) return;
        try { Directory.Delete(staging, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响已完成的更新。
        }
    }

    private static Process? StartServer(UpdateInstallPlan plan, bool setStartupMarker)
    {
        var startInfo = new ProcessStartInfo(plan.DotnetHostPath)
        {
            WorkingDirectory = plan.DestinationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(plan.ServerAssemblyPath);
        foreach (var argument in plan.ServerArguments) startInfo.ArgumentList.Add(argument);
        if (setStartupMarker && plan.StartupMarkerPath is { Length: > 0 } markerPath)
        {
            try { if (File.Exists(markerPath)) File.Delete(markerPath); } catch (IOException) { }
            startInfo.Environment[StartupMarkerEnvVar] = markerPath;
        }
        return Process.Start(startInfo);
    }

    /// <summary>轮询启动标记：新版本在超时内完成启动即通过并清理标记；进程提前退出或超时视为失败。</summary>
    internal static async Task<bool> WaitForStartupMarkerAsync(string markerPath, Process process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartupHealthCheckTimeout);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (File.Exists(markerPath))
                {
                    try { File.Delete(markerPath); } catch (IOException) { }
                    return true;
                }
                if (process.HasExited) return false;
                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 超时或外部取消。
        }
        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已退出或无法终止：回滚仍继续执行。
        }
    }

    private static void TryDeleteBackup(string? backupRoot)
    {
        if (backupRoot is null) return;
        try { Directory.Delete(backupRoot, recursive: true); }
        catch (IOException) { /* 备份目录删除失败不影响已完成的更新。 */ }
    }

    /// <summary>复制发布目录；短暂文件锁会持续重试，确保旧进程完全释放原生 DLL。配置文件一律不覆盖。</summary>
    public static async Task ApplyFilesAsync(string source, string destination, CancellationToken ct)
    {
        var (backupRoot, _) = await ApplyFilesWithRollbackAsync(source, destination, ct, CopyRetryCount);
        TryDeleteBackup(backupRoot);
    }

    /// <summary>
    /// 带回滚的文件替换：每个被覆盖的目标文件先备份到目标目录旁的 rollback 目录，
    /// 任何失败都会恢复已处理的文件（恢复备份、删除新建）。
    /// 成功时返回备份目录与回滚日志，由调用方决定何时删除备份（健康检查通过后）。
    /// </summary>
    internal static async Task<(string BackupRoot, IReadOnlyList<(string Relative, bool Existed)> Journal)>
        ApplyFilesWithRollbackAsync(string source, string destination, CancellationToken ct, int maxCopyRetries)
    {
        Directory.CreateDirectory(destination);
        var backupRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(destination))!,
            $"{Path.GetFileName(Path.GetFullPath(destination))}.rollback-{Guid.NewGuid():N}");
        var journal = new List<(string Relative, bool Existed)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (NeverOverwriteFiles.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var existed = File.Exists(target);
                if (existed)
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                }
                journal.Add((relative, existed));
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Copy(file, target, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < maxCopyRetries)
                    {
                        await Task.Delay(250, ct);
                    }
                }
            }

            CleanStaleRuntimeFiles(source, destination, backupRoot, journal);
            return (backupRoot, journal);
        }
        catch
        {
            RestoreBackups(backupRoot, destination, journal);
            throw;
        }
    }

    /// <summary>回滚：恢复本次更新覆盖的文件、删除本次新建的文件；单个文件失败不阻断其余恢复。</summary>
    internal static void RestoreBackups(
        string backupRoot, string destination, IReadOnlyList<(string Relative, bool Existed)> journal)
    {
        foreach (var (relative, existed) in journal)
        {
            var target = Path.Combine(destination, relative);
            try
            {
                if (existed)
                {
                    var backup = Path.Combine(backupRoot, relative);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(backup, target, overwrite: true);
                    }
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 尽力而为；失败的文件保留现场（backupRoot 不删除）供人工处置。
            }
        }
    }

    /// <summary>发布输出中属于运行时产物的平面文件扩展名（用于陈旧文件清理白名单）。</summary>
    private static readonly string[] RuntimeFileExtensions =
        [".dll", ".pdb", ".exe", ".deps.json", ".runtimeconfig.json", ".staticwebassets.endpoints.json"];

    /// <summary>
    /// 保守清理：删除目标目录根部的平面文件（扩展名在运行时产物白名单内且源中已不存在），
    /// 以及 runtimes/ 子目录中源里已不存在的文件（纯 NuGet 运行时载荷，旧原生库可能带漏洞）。
    /// 其余子目录（data/、keys/、updates/、wwwroot/ 等）一律不动，配置文件永远保留。
    /// 被删除的文件先备份并记入回滚日志，保证健康检查失败回滚后旧版本仍可启动。
    /// </summary>
    internal static void CleanStaleRuntimeFiles(
        string source,
        string destination,
        string backupRoot,
        List<(string Relative, bool Existed)> journal)
    {
        try
        {
            var sourceFiles = new HashSet<string>(
                Directory.EnumerateFiles(source).Select(file => Path.GetFileName(file)!), StringComparer.OrdinalIgnoreCase);
            foreach (var target in Directory.EnumerateFiles(destination))
            {
                var name = Path.GetFileName(target);
                if (sourceFiles.Contains(name)) continue;
                if (NeverOverwriteFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (!RuntimeFileExtensions.Any(extension =>
                        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) continue;
                BackupThenDelete(target, name, backupRoot, journal);
            }

            CleanStaleRuntimesPayload(source, destination, backupRoot, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响更新流程。
        }
    }

    /// <summary>删除前先备份到回滚目录并记入日志；回滚时 RestoreBackups 会把备份复制回原位。</summary>
    private static void BackupThenDelete(
        string target,
        string relative,
        string backupRoot,
        List<(string Relative, bool Existed)> journal)
    {
        var backup = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(target, backup, overwrite: true);
        File.Delete(target);
        journal.Add((relative, true));
    }

    private static void CleanStaleRuntimesPayload(
        string source,
        string destination,
        string backupRoot,
        List<(string Relative, bool Existed)> journal)
    {
        var sourceRuntimes = Path.Combine(source, "runtimes");
        var targetRuntimes = Path.Combine(destination, "runtimes");
        if (!Directory.Exists(sourceRuntimes) || !Directory.Exists(targetRuntimes)) return;
        var sourceRuntimeFiles = new HashSet<string>(
            Directory.EnumerateFiles(sourceRuntimes, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(sourceRuntimes, file)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var target in Directory.EnumerateFiles(targetRuntimes, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(targetRuntimes, target);
            if (sourceRuntimeFiles.Contains(relative)) continue;
            BackupThenDelete(target, Path.Combine("runtimes", relative), backupRoot, journal);
        }
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken ct)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(ct).WaitAsync(ProcessExitTimeout, ct);
        }
        catch (ArgumentException)
        {
            // 旧进程已经退出，直接安装。
        }
    }

    private static void Validate(UpdateInstallPlan plan)
    {
        var source = Path.GetFullPath(plan.SourceDirectory);
        var destination = Path.GetFullPath(plan.DestinationDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"更新源目录不存在：{source}");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新源目录不能与运行目录相同。");
        if (Path.GetPathRoot(destination) == destination)
            throw new InvalidDataException("拒绝把磁盘根目录作为更新目标。");
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能阻止已验证的更新流程。
        }
    }
}
