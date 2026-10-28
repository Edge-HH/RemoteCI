using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RemoteCI.Guard;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 应用守护的插件端：按服务端下发的策略把随包附带的 RemoteCI.Guard.exe 部署到
/// %LOCALAPPDATA%\RemoteCI\Guard 并登记登录自启，写入守护配置与运行标记；守护进程恢复 ClassIsland 后，
/// 在新实例中用原生对话框提示，并把守护事件上报服务端。策略关闭时停止守护进程并清理目录与备份。
/// </summary>
public sealed class AppGuardService : IDisposable
{
    public const string NoticeHeader = "应用守护";
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdatingMarkerLifetime = TimeSpan.FromMinutes(15);

    private readonly AppGuardStore _store;
    private readonly IAppGuardHost _host;
    private readonly ILogger<AppGuardService> _logger;
    private readonly object _lock = new();
    private Timer? _timer;
    private int _flushing;
    private bool _msixNoticeRecorded;

    public AppGuardService(AppGuardStore store, IAppGuardHost host, ILogger<AppGuardService> logger, string? directory = null)
    {
        _store = store;
        _host = host;
        _logger = logger;
        Directory = directory ?? GuardFiles.DefaultDirectory();
    }

    public string Directory { get; }

    /// <summary>上报守护事件的通道（云端连接）；返回 false 表示未送达，下次重试。</summary>
    public Func<AppGuardEventBatch, Task<bool>>? EventSender { get; set; }

    /// <summary>当前云端开关、服务器地址和长期设备凭据；仅在内存中读取并加密同步给守护代理。</summary>
    public Func<(bool Enabled, string ServerUrl, string? Token)>? ConnectionInfoProvider { get; set; }

    public string GuardExecutablePath => ExecutablePath(Directory, _store.Current?.ProcessName);

    internal static string ExecutablePath(string directory, string? processName) =>
        Path.Combine(directory, GuardFiles.ExecutableNameFor(processName));

    public void Start()
    {
        _store.Changed += Apply;
        _ = Task.Run(() =>
        {
            try { Apply(); }
            catch (Exception ex) { _logger.LogError(ex, "应用守护初始部署失败"); }
        });
        ShowPendingNotice();
        _timer = new Timer(_ => Maintain(), null, MaintenanceInterval, MaintenanceInterval);
    }

    public void Stop()
    {
        _store.Changed -= Apply;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>ClassIsland 正常退出前调用：守护进程据此区分正常退出（按延时自启）与意外退出（立即重启）。</summary>
    public void MarkExiting()
    {
        if (!IsDeployed()) return;
        var path = Path.Combine(Directory, GuardFiles.State);
        var current = GuardJson.ReadFile<GuardRunState>(path);
        // 远程升级 ClassIsland 时已写了 updating，退出时不能覆盖成 exiting。
        if (current is { State: GuardRunStates.Updating } &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - current.AtUnixMs < UpdatingMarkerLifetime.TotalMilliseconds)
            return;
        WriteState(GuardRunStates.Exiting);
    }

    /// <summary>远程升级 ClassIsland 即将重启前调用：守护进程在升级期间不干预。</summary>
    public void MarkUpdating()
    {
        if (IsDeployed()) WriteState(GuardRunStates.Updating);
    }

    /// <summary>收到服务端下发的策略：落盘后由 <see cref="AppGuardStore.Changed"/> 触发部署或清理。</summary>
    public void ApplyPolicy(AppGuardPolicy policy) => _store.Apply(policy);

    public void Apply()
    {
        if (_store.Current is not { } policy) return;
        lock (_lock)
        {
            try
            {
                if (policy.Enabled) Deploy(policy, allowElevation: true);
                else if (HasStopRequest()) StopWithoutCleanup();
                else Teardown();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogWarning(ex, "应用守护{Action}失败", policy.Enabled ? "部署" : "清理");
            }
        }
    }

    private bool HasStopRequest() => File.Exists(Path.Combine(Directory, GuardFiles.StopRequested));

    private void StopWithoutCleanup()
    {
        var config = GuardJson.ReadFile<GuardConfig>(Path.Combine(Directory, GuardFiles.Policy));
        var target = ExecutablePath(Directory, config?.ProcessName);
        if (File.Exists(target)) _host.StopGuard(target);
        _host.SetRunKey(null);
        _logger.LogInformation("应用守护已关闭，保留 {Path} 供之后重新启用", Directory);
    }

    /// <summary>
    /// 每分钟：守护程序或配置消失时重新部署，插件包里的守护版本更新时替换旧版，进程被结束时重新拉起，并上报守护事件。
    /// 守护刚执行过远程关闭或卸载时不干预，等服务端下发停用策略。
    /// </summary>
    internal void Maintain()
    {
        if (_store.Current is { Enabled: true } policy)
        {
            lock (_lock)
            {
                try
                {
                    if (StoppedByGuard()) { }
                    else if (NeedsRedeploy())
                    {
                        _logger.LogInformation("应用守护程序缺失或版本过旧，重新部署");
                        Deploy(policy, allowElevation: false);
                    }
                    else
                    {
                        SyncConnection();
                        if (!_host.IsGuardRunning(GuardExecutablePath))
                        {
                            _logger.LogInformation("应用守护进程未运行，重新启动");
                            _host.StartGuard(GuardExecutablePath, Directory);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "维护应用守护进程失败");
                }
            }
        }
        _ = FlushEventsAsync();
    }

    /// <summary>
    /// 系统管理员远程要求立即更新：结束守护，用插件包随附版本强制覆盖后重新启动，把失败原因原样返回。
    /// 普通守护代理在用户目录内，替换不需要 UAC；增强服务的安装或变更仍留给策略下发处理。
    /// </summary>
    public CommandResult UpdateNow()
    {
        if (_store.Current is not { Enabled: true } policy)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "该设备未启用应用守护");
        var bundled = _host.BundledGuardPath;
        if (!File.Exists(bundled))
            return CommandResult.Failure(CommandResultCodes.InternalError, $"插件包中缺少 {GuardFiles.ExecutableName}，请重新安装 RemoteCI 插件");
        lock (_lock)
        {
            try
            {
                var target = GuardExecutablePath;
                if (File.Exists(target)) _host.StopGuard(target);
                System.IO.Directory.CreateDirectory(Directory);
                File.Copy(bundled, target, true);
                Deploy(policy, allowElevation: false);
                if (!_host.IsGuardRunning(target))
                    return CommandResult.Failure(CommandResultCodes.InternalError, "守护程序已更新，但未能启动，请查看 ClassIsland 日志");
                var version = _host.GuardVersion(target);
                _logger.LogInformation("系统管理员远程更新应用守护：{Version}", version);
                return new CommandResult
                {
                    Success = true,
                    Code = CommandResultCodes.Ok,
                    Message = string.IsNullOrEmpty(version) ? "守护已更新并重新启动" : $"守护已更新到 {version} 并重新启动",
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogWarning(ex, "远程更新应用守护失败");
                return CommandResult.Failure(CommandResultCodes.InternalError, $"更新守护失败：{ex.Message}");
            }
        }
    }

    /// <summary>守护通过云端通道执行了关闭或卸载：本地配置已被它改为停用，或留下了保留式关闭标记。</summary>
    private bool StoppedByGuard() =>
        HasStopRequest() || GuardJson.ReadFile<GuardConfig>(Path.Combine(Directory, GuardFiles.Policy)) is { Enabled: false };

    private bool NeedsRedeploy()
    {
        var target = GuardExecutablePath;
        var bundled = _host.BundledGuardPath;
        if (!File.Exists(bundled)) return false;
        return !File.Exists(Path.Combine(Directory, GuardFiles.Policy)) || !File.Exists(target) || !SameContent(bundled, target);
    }

    /// <summary>
    /// 把 events.jsonl 改名为待发送文件后再读，避免与守护进程同时写；送达后删除，未送达保留到下次。
    /// </summary>
    public async Task FlushEventsAsync()
    {
        if (EventSender is not { } sender || Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var sending = Path.Combine(Directory, GuardFiles.EventsSending);
            var pending = Path.Combine(Directory, GuardFiles.Events);
            if (!File.Exists(sending))
            {
                if (!File.Exists(pending)) return;
                File.Move(pending, sending);
            }
            var events = File.ReadAllLines(sending)
                .Select(line => GuardJson.Deserialize<GuardEventLine>(line))
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Kind))
                .Select(x => new AppGuardEvent { Kind = x!.Kind, AtUnixMs = x.AtUnixMs, Detail = x.Detail })
                .ToList();
            if (events.Count == 0 || await sender(new AppGuardEventBatch { Events = events }))
                File.Delete(sending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "读取应用守护事件失败，下次重试");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    /// <summary>守护进程恢复 ClassIsland 后留下提示文件：在这个新实例里用 ClassIsland 原生对话框告知使用者。</summary>
    public void ShowPendingNotice()
    {
        var path = Path.Combine(Directory, GuardFiles.PendingNotice);
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        _host.ShowNotice(NoticeHeader, AppGuardPolicy.RestoredNotice(_store.Current?.OrganizationName));
    }

    internal static GuardConfig BuildConfig(AppGuardPolicy policy, AppGuardHostInfo info, long nowUnixMs)
    {
        policy = policy.Normalized();
        var roots = new List<string> { info.PackageRoot };
        foreach (var root in info.DataRoots)
        {
            if (roots.Any(x => GuardBackup.IsUnder(root, x))) continue;
            roots.Add(root);
        }
        return new GuardConfig
        {
            Enabled = policy.Enabled,
            EnhancedMode = policy.EnhancedMode,
            RandomBackupPath = policy.RandomBackupPath,
            BackupDirectoryName = policy.BackupDirectoryName,
            ProcessName = policy.ProcessName,
            RestartAfterExit = policy.RestartAfterExit,
            RestartDelayMinutes = policy.RestartAfterExitDelayMinutes,
            RestartOnCrash = policy.RestartOnCrash,
            // MSIX 包安装在系统保护目录，无法备份与还原。
            PreventDeletion = policy.PreventDeletion && !info.IsMsix,
            BackupIntervalHours = policy.BackupIntervalHours,
            OrganizationName = policy.OrganizationName,
            LaunchPath = info.LaunchPath,
            PackageRoot = info.PackageRoot,
            // 有启动器时只以启动器判断是否被删除：升级会换掉 app-版本 目录，以它为准会误判。
            Sentinels = [GuardBackup.IsUnder(info.LaunchPath, info.PackageRoot) ? info.LaunchPath : info.ProcessPath],
            BackupRoots = roots,
            BackupPaths = policy.BackupPaths.ToList(),
            Excludes = info.Excludes.ToList(),
            ClassIslandVersion = info.ClassIslandVersion,
            UpdatedAtUnixMs = nowUnixMs,
        };
    }

    /// <param name="allowElevation">是否允许为增强守护请求 UAC；每分钟的维护不弹授权，避免被拒绝后反复打扰。</param>
    private void Deploy(AppGuardPolicy policy, bool allowElevation)
    {
        var info = _host.Describe();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = BuildConfig(policy, info, now);
        var previous = GuardJson.ReadFile<GuardConfig>(Path.Combine(Directory, GuardFiles.Policy));
        config.RandomBackupPath = previous?.RandomBackupPath == true;
        config.ProtectedBackupPath = previous?.ProtectedBackupPath;
        config.ProtectedBackupReady = previous?.ProtectedBackupReady == true;
        GuardBackup.ConfigureProtectedBackup(config, policy.RandomBackupPath,
            message => _logger.LogWarning("{Message}", message));
        System.IO.Directory.CreateDirectory(Directory);
        try { File.Delete(Path.Combine(Directory, GuardFiles.StopRequested)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "清理过期的守护关闭标记失败");
        }
        GuardJson.WriteFile(Path.Combine(Directory, GuardFiles.Policy), config);
        WriteState(GuardRunStates.Running);
        if (policy.PreventDeletion && info.IsMsix && !_msixNoticeRecorded)
        {
            _msixNoticeRecorded = true;
            AppendEvent(GuardEventKinds.BackupFailed, "当前 ClassIsland 为 MSIX 安装，不支持禁止删除，其余守护项照常生效");
        }

        var target = GuardExecutablePath;
        var previousTarget = ExecutablePath(Directory, previous?.ProcessName);
        var renamed = !string.Equals(previousTarget, target, StringComparison.OrdinalIgnoreCase);
        if (renamed && File.Exists(previousTarget)) _host.StopGuard(previousTarget);
        var bundled = _host.BundledGuardPath;
        if (File.Exists(bundled) && (!File.Exists(target) || !SameContent(bundled, target)))
        {
            // 运行中的旧版守护进程占用着 exe，先结束它才能替换。
            if (File.Exists(target)) _host.StopGuard(target);
            File.Copy(bundled, target, true);
            _logger.LogInformation("已部署应用守护进程到 {Path}", target);
        }
        if (!File.Exists(target))
        {
            _logger.LogWarning("插件包中缺少 {File}，应用守护无法运行", GuardFiles.ExecutableName);
            return;
        }
        var enhancedMarker = _host.EnhancedMarkerPath;
        var enhanced = File.Exists(enhancedMarker) ? GuardJson.ReadFile<EnhancedGuardConfig>(enhancedMarker) : null;
        var enhancedCurrent = enhanced is not null &&
            string.Equals(Path.GetFullPath(enhanced.AgentDirectory), Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(enhanced.AgentExecutablePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        var enhancedPending = policy.EnhancedMode ? !enhancedCurrent : File.Exists(enhancedMarker);
        // 不允许授权时保留增强服务现状，也不删除改名前的程序，留给下次策略下发处理。
        var enhancedFailed = enhancedPending && !allowElevation;
        if (!allowElevation) { }
        else if (policy.EnhancedMode && !enhancedCurrent)
        {
            enhancedFailed = !_host.ConfigureEnhanced(true, target, Directory);
            if (enhancedFailed)
                AppendEvent("enhanced-failed", "增强守护安装或路径更新未完成，已保留普通守护；请允许管理员授权后重试");
        }
        else if (!policy.EnhancedMode && File.Exists(enhancedMarker))
        {
            enhancedFailed = !_host.ConfigureEnhanced(false, target, Directory);
            if (enhancedFailed) AppendEvent("enhanced-failed", "增强守护服务卸载未完成，请允许管理员授权后重试");
        }
        _host.SetRunKey($"\"{target}\"");
        SyncConnection();
        if (!_host.IsGuardRunning(target)) _host.StartGuard(target, Directory);
        if (renamed && !enhancedFailed && File.Exists(previousTarget))
        {
            try { File.Delete(previousTarget); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "清理改名前的应用守护程序失败：{Path}", previousTarget);
            }
        }
    }

    private void SyncConnection()
    {
        if (ConnectionInfoProvider?.Invoke() is not { } info || !System.IO.Directory.Exists(Directory)) return;
        string? protectedToken = null;
        if (!string.IsNullOrEmpty(info.Token))
        {
            var plain = System.Text.Encoding.UTF8.GetBytes(info.Token);
            var encrypted = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plain, System.Text.Encoding.UTF8.GetBytes("RemoteCI.CloudToken.v1"), DataProtectionScope.CurrentUser)
                : plain;
            protectedToken = Convert.ToBase64String(encrypted);
        }
        var path = Path.Combine(Directory, GuardFiles.Connection);
        GuardJson.WriteFile(path, new GuardConnectionConfig
        {
            Enabled = info.Enabled,
            ServerUrl = info.ServerUrl,
            ProtectedToken = protectedToken,
            UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        FileProtection.RestrictToCurrentUser(path);
    }

    private void Teardown()
    {
        var config = GuardJson.ReadFile<GuardConfig>(Path.Combine(Directory, GuardFiles.Policy));
        var target = ExecutablePath(Directory, config?.ProcessName);
        if (File.Exists(target)) _host.StopGuard(target);
        var enhancedMarker = _host.EnhancedMarkerPath;
        var enhanced = File.Exists(enhancedMarker) ? GuardJson.ReadFile<EnhancedGuardConfig>(enhancedMarker) : null;
        if (File.Exists(enhancedMarker))
        {
            var enhancedTarget = !string.IsNullOrWhiteSpace(enhanced?.AgentExecutablePath) ? enhanced.AgentExecutablePath : target;
            if (!string.Equals(enhancedTarget, target, StringComparison.OrdinalIgnoreCase) && File.Exists(enhancedTarget))
                _host.StopGuard(enhancedTarget);
            _host.ConfigureEnhanced(false, enhancedTarget, Directory);
        }
        _host.SetRunKey(null);
        if (config is not null)
        {
            foreach (var backup in GuardBackup.BackupTargets(config, Directory, null).Skip(1))
            {
                if (System.IO.Directory.Exists(backup)) System.IO.Directory.Delete(backup, true);
            }
        }
        if (!System.IO.Directory.Exists(Directory)) return;
        // 未上报的事件随目录一起丢弃即可：关闭守护后不再需要。
        System.IO.Directory.Delete(Directory, true);
        _logger.LogInformation("应用守护已关闭，已清理 {Path}", Directory);
    }

    private bool IsDeployed() => _store.Current is { Enabled: true } && File.Exists(Path.Combine(Directory, GuardFiles.Policy));

    private void WriteState(string state)
    {
        try
        {
            GuardJson.WriteFile(Path.Combine(Directory, GuardFiles.State), new GuardRunState
            {
                Pid = _host.CurrentProcessId,
                State = state,
                AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "写入应用守护运行标记失败");
        }
    }

    private void AppendEvent(string kind, string detail)
    {
        var line = GuardJson.Serialize(new GuardEventLine { Kind = kind, AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Detail = detail });
        File.AppendAllText(Path.Combine(Directory, GuardFiles.Events), line + "\n");
    }

    private static bool SameContent(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
}
