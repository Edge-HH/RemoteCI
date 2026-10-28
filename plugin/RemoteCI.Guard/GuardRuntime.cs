using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RemoteCI.Guard;

/// <summary>每 5 秒检查一次 ClassIsland，并按 <see cref="GuardStateMachine"/> 的决定启动、恢复或备份。</summary>
internal sealed class GuardRuntime : IDisposable
{
    private const int TickMs = 5_000;
    private const long LogMaxBytes = 1024 * 1024;

    private readonly string _directory;
    private readonly GuardStateMachine _machine = new();
    private readonly object _tickLock = new();
    /// <summary>与插件上报的版本同一口径（InformationalVersion 去掉构建元数据），服务端据此判断守护是否为插件随附的最新版。</summary>
    internal static string Version { get; } =
        typeof(GuardRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(GuardRuntime).Assembly.GetName().Version?.ToString() ?? string.Empty;

    private readonly string _version = Version;
    private Timer? _timer;
    private GuardCloudClient? _cloud;
    private volatile bool _stopping;
    private int _backupRunning;
    private long _lastBackupAttemptMs;
    private long _lastRunKeyCheckMs;
    private GuardStatus _status = new();

    public GuardRuntime(string directory)
    {
        _directory = directory;
    }

    public event Action? Exited;

    public void Start()
    {
        Directory.CreateDirectory(_directory);
        _status = GuardJson.ReadFile<GuardStatus>(Path.Combine(_directory, GuardFiles.Status)) ?? new GuardStatus();
        Log($"守护进程启动，版本 {_version}");
        _cloud = new GuardCloudClient(_directory)
        {
            CommandHandler = HandleRemoteCommand,
            CommandHandled = HandleRemoteCommandHandled,
            StatusChanged = (connected, status) =>
            {
                _status.CloudConnected = connected;
                _status.CloudStatus = status;
            },
            Log = Log,
        };
        _cloud.Start();
        _timer = new Timer(_ => Tick(), null, 0, TickMs);
    }

    public void StopForSessionEnd()
    {
        if (_stopping) return;
        _stopping = true;
        _timer?.Dispose();
        _cloud?.Dispose();
        Log("会话结束，守护进程停止");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cloud?.Dispose();
    }

    private void Tick()
    {
        if (_stopping || !Monitor.TryEnter(_tickLock)) return;
        try
        {
            var policy = GuardJson.ReadFile<GuardConfig>(Path.Combine(_directory, GuardFiles.Policy));
            if (policy is null || !policy.Enabled)
            {
                Log(policy is null ? "找不到守护配置，退出" : "应用守护已关闭，注销登录自启并退出");
                RemoveRunKey();
                _stopping = true;
                _timer?.Dispose();
                Exited?.Invoke();
                return;
            }

            if (policy.RandomBackupPath && !policy.ProtectedBackupReady)
            {
                GuardBackup.ConfigureProtectedBackup(policy, true, Log);
                TryWrite(Path.Combine(_directory, GuardFiles.Policy), policy);
            }
            var now = GuardJson.NowUnixMs();
            EnsureRunKey(now);
            var state = GuardJson.ReadFile<GuardRunState>(Path.Combine(_directory, GuardFiles.State));
            var running = IsClassIslandRunning(policy, state);
            var observation = new GuardObservation
            {
                NowMs = now,
                Running = running,
                InstallPresent = IsInstallPresent(policy),
                RunState = state?.State,
                RunStateAtMs = state?.AtUnixMs ?? 0,
            };
            var decision = _machine.Tick(observation, policy);
            Execute(decision, policy, now);
            if (running && policy.PreventDeletion) MaybeBackup(policy, now);
            WriteStatus(now);
        }
        catch (Exception ex)
        {
            Log($"检查失败：{ex}");
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    private void Execute(GuardDecision decision, GuardConfig policy, long now)
    {
        switch (decision.Action)
        {
            case GuardAction.Restore:
                Log("检测到 ClassIsland 被删除，开始从备份恢复");
                var restored = GuardBackup.Restore(policy, _directory, Log);
                if (!restored.Success)
                {
                    _machine.RestoreFailed(now);
                    Log($"恢复失败：{restored.Error}");
                    RecordEvent(GuardEventKinds.RestoreFailed, restored.Error);
                    return;
                }
                Log($"已恢复 {restored.CopiedFiles} 个文件");
                // 先留下提示，再启动：新实例里的插件读到它后用 ClassIsland 原生对话框提示。
                TryWrite(Path.Combine(_directory, GuardFiles.PendingNotice), new GuardNotice { AtUnixMs = now });
                if (TryLaunch(policy, out var restoreError))
                    RecordEvent(GuardEventKinds.Restored, $"已恢复 {restored.CopiedFiles} 个文件并重新启动");
                else
                {
                    RecordEvent(GuardEventKinds.Restored, $"已恢复 {restored.CopiedFiles} 个文件，但启动失败：{restoreError}");
                    _machine.LaunchFailed(now, null, null);
                }
                return;
            case GuardAction.Launch:
                Log($"启动 ClassIsland：{decision.Detail ?? decision.EventKind ?? "按策略启动"}");
                if (TryLaunch(policy, out var error))
                {
                    if (decision.EventKind is { } kind) RecordEvent(kind, decision.Detail);
                }
                else
                {
                    Log($"启动失败：{error}");
                    _machine.LaunchFailed(now, decision.EventKind, decision.Detail);
                }
                return;
            default:
                if (decision.EventKind is { } only)
                {
                    Log(decision.Detail ?? only);
                    RecordEvent(only, decision.Detail);
                }
                return;
        }
    }

    private void MaybeBackup(GuardConfig policy, long now)
    {
        var manifest = GuardJson.ReadFile<GuardBackupManifest>(GuardBackup.ManifestPath(_directory));
        if (!GuardBackup.IsDue(policy, manifest, _lastBackupAttemptMs, now)) return;
        if (Interlocked.Exchange(ref _backupRunning, 1) == 1) return;
        _lastBackupAttemptMs = now;
        Task.Run(() =>
        {
            try
            {
                Log("开始备份 ClassIsland");
                var result = GuardBackup.Mirror(policy, _directory, Log);
                if (result.Success)
                {
                    _lastBackupAttemptMs = 0;
                    _status.LastBackupUnixMs = GuardJson.NowUnixMs();
                    _status.LastBackupBytes = result.TotalBytes;
                    _status.LastError = null;
                    var detail = $"共 {result.FileCount} 个文件（{result.TotalBytes / 1024 / 1024} MB），本次复制 {result.CopiedFiles} 个" +
                                 (result.SkippedFiles > 0 ? $"，{result.SkippedFiles} 个被占用已跳过" : string.Empty);
                    Log($"备份完成：{detail}");
                    // 版本变化或首次备份才上报，定时增量备份只写日志，避免刷屏。
                    if (manifest is null || !string.Equals(manifest.ClassIslandVersion, policy.ClassIslandVersion, StringComparison.OrdinalIgnoreCase))
                        RecordEvent(GuardEventKinds.BackupCompleted, detail);
                }
                else
                {
                    _status.LastError = result.Error;
                    Log($"备份失败：{result.Error}");
                    RecordEvent(GuardEventKinds.BackupFailed, result.Error);
                }
            }
            catch (Exception ex)
            {
                _status.LastError = ex.Message;
                Log($"备份异常：{ex}");
                RecordEvent(GuardEventKinds.BackupFailed, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _backupRunning, 0);
            }
        });
    }

    private static bool IsInstallPresent(GuardConfig policy) =>
        Directory.Exists(policy.PackageRoot) &&
        policy.Sentinels.Where(x => !string.IsNullOrWhiteSpace(x)).All(File.Exists);

    /// <summary>优先按插件记录的 pid 判断；否则查找安装目录下的任意 ClassIsland 进程（含启动器和更新器）。</summary>
    private static bool IsClassIslandRunning(GuardConfig policy, GuardRunState? state)
    {
        if (state is { Pid: > 0 } && IsProcessUnder(state.Pid, policy.PackageRoot)) return true;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Process.GetCurrentProcess().Id) continue;
                if (!process.ProcessName.StartsWith("ClassIsland", StringComparison.OrdinalIgnoreCase) &&
                    !process.ProcessName.StartsWith("Updater", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsProcessUnder(process.Id, policy.PackageRoot)) return true;
            }
        }
        return false;
    }

    private static bool IsProcessUnder(int pid, string root)
    {
        var path = ProcessPath(pid);
        return path is not null && GuardBackup.IsUnder(path, root);
    }

    private static string? ProcessPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private GuardCommandResult HandleRemoteCommand(GuardCommand command)
    {
        lock (_tickLock)
        {
            if (command.Command is GuardCommands.StopAppGuard or GuardCommands.UninstallAppGuard)
                return PrepareRemoteStop(command.Command == GuardCommands.UninstallAppGuard);
            var policy = GuardJson.ReadFile<GuardConfig>(Path.Combine(_directory, GuardFiles.Policy));
            if (policy is not { Enabled: true }) return GuardCommandResult.Failure("PLUGIN_OFFLINE", "应用守护未启用");
            var state = GuardJson.ReadFile<GuardRunState>(Path.Combine(_directory, GuardFiles.State));
            if (IsClassIslandRunning(policy, state)) return GuardCommandResult.Ok("ClassIsland 已在运行");
            if (!IsInstallPresent(policy)) return GuardCommandResult.Failure("INTERNAL_ERROR", "ClassIsland 启动器不存在，等待守护恢复");
            if (!TryLaunch(policy, out var error)) return GuardCommandResult.Failure("INTERNAL_ERROR", $"启动失败：{error}");
            RecordEvent("remote-launched", "系统管理员通过守护通道远程打开 ClassIsland");
            return GuardCommandResult.Ok("已向 ClassIsland 发出启动请求");
        }
    }

    private GuardCommandResult PrepareRemoteStop(bool uninstall)
    {
        var path = Path.Combine(_directory, GuardFiles.Policy);
        var policy = GuardJson.ReadFile<GuardConfig>(path);
        if (policy is not { Enabled: true }) return GuardCommandResult.Failure("PLUGIN_OFFLINE", "应用守护未启用");
        if (uninstall && !TryUninstallEnhanced(policy, out var uninstallError))
            return GuardCommandResult.Failure("INTERNAL_ERROR", uninstallError ?? "增强守护卸载未完成");
        var stopMarker = Path.Combine(_directory, GuardFiles.StopRequested);
        if (!uninstall)
        {
            try { File.WriteAllText(stopMarker, GuardJson.NowUnixMs().ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GuardCommandResult.Failure("INTERNAL_ERROR", $"无法标记保留式关闭：{ex.Message}");
            }
        }
        else
        {
            try { File.Delete(stopMarker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        policy.Enabled = false;
        if (uninstall) policy.EnhancedMode = false;
        policy.UpdatedAtUnixMs = GuardJson.NowUnixMs();
        try { GuardJson.WriteFile(path, policy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!uninstall)
            {
                try { File.Delete(stopMarker); }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException) { }
            }
            return GuardCommandResult.Failure("INTERNAL_ERROR", $"无法保存守护停用状态：{ex.Message}");
        }
        return GuardCommandResult.Ok(uninstall ? "应用守护已卸载" : "应用守护已关闭");
    }

    private bool TryUninstallEnhanced(GuardConfig policy, out string? error)
    {
        error = null;
        var marker = Path.Combine(EnhancedGuardSetup.ProgramDataDirectory(), "enhanced.json");
        if (!policy.EnhancedMode && !File.Exists(marker)) return true;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = typeof(GuardRuntime).Assembly.Location,
                Arguments = "--uninstall-enhanced",
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = _directory,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                error = "无法启动增强守护卸载程序";
                return false;
            }
            process.WaitForExit();
            if (process.ExitCode == 0) return true;
            error = $"增强守护卸载程序返回错误 {process.ExitCode}";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = $"增强守护卸载已取消或失败：{ex.Message}";
            return false;
        }
    }

    private void HandleRemoteCommandHandled(GuardCommand command, GuardCommandResult result)
    {
        if (!result.Success ||
            command.Command is not (GuardCommands.StopAppGuard or GuardCommands.UninstallAppGuard)) return;
        var uninstall = command.Command == GuardCommands.UninstallAppGuard;
        var policy = GuardJson.ReadFile<GuardConfig>(Path.Combine(_directory, GuardFiles.Policy));
        RemoveRunKey();
        _stopping = true;
        _timer?.Dispose();
        if (uninstall && policy is not null) StartDeferredCleanup(policy);
        Log(uninstall ? "应用守护已卸载，守护进程退出" : "应用守护已关闭，守护进程退出");
        Exited?.Invoke();
    }

    private void StartDeferredCleanup(GuardConfig policy)
    {
        try
        {
            var targets = GuardBackup.BackupTargets(policy, _directory, null).Skip(1).ToList();
            targets.Add(_directory);
            var remove = string.Join(" & ", targets.Select(path => $"rmdir /s /q \"{path}\""));
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = $"/d /c timeout /t 2 /nobreak >nul & {remove}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log($"守护文件延迟清理失败：{ex.Message}");
        }
    }

    private bool TryLaunch(GuardConfig policy, out string? error)
    {
        error = null;
        if (_stopping) { error = "会话正在结束"; return false; }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = policy.LaunchPath,
                WorkingDirectory = Path.GetDirectoryName(policy.LaunchPath) ?? policy.PackageRoot,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    private void EnsureRunKey(long now)
    {
        if (now - _lastRunKeyCheckMs < 60_000) return;
        _lastRunKeyCheckMs = now;
        try
        {
            var command = $"\"{typeof(GuardRuntime).Assembly.Location}\"";
            using var key = Registry.CurrentUser.CreateSubKey(GuardFiles.RunKeyPath);
            if (!string.Equals(key?.GetValue(GuardFiles.RunValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                key?.SetValue(GuardFiles.RunValueName, command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log($"写入登录自启失败：{ex.Message}");
        }
    }

    private void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GuardFiles.RunKeyPath, true);
            key?.DeleteValue(GuardFiles.RunValueName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log($"删除登录自启失败：{ex.Message}");
        }
    }

    private void RecordEvent(string kind, string? detail)
    {
        var line = GuardJson.Serialize(new GuardEventLine { Kind = kind, AtUnixMs = GuardJson.NowUnixMs(), Detail = detail });
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, GuardFiles.Events), line + "\n", new UTF8Encoding(false));
                _cloud?.NotifyEvent();
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    private void WriteStatus(long now)
    {
        _status.Pid = Process.GetCurrentProcess().Id;
        _status.Version = _version;
        _status.HeartbeatUnixMs = now;
        TryWrite(Path.Combine(_directory, GuardFiles.Status), _status);
    }

    private void TryWrite<T>(string path, T value)
    {
        try { GuardJson.WriteFile(path, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"写入 {Path.GetFileName(path)} 失败：{ex.Message}"); }
    }

    private void Log(string message)
    {
        try
        {
            var path = Path.Combine(_directory, GuardFiles.Log);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > LogMaxBytes)
            {
                var old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
