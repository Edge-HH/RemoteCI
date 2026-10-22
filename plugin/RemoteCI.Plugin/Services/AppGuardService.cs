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

    public string GuardExecutablePath => Path.Combine(Directory, GuardFiles.ExecutableName);

    /// <summary>设置页显示的一句话状态。</summary>
    public string Summary
    {
        get
        {
            if (_store.Current is not { } policy) return "应用守护：未由管理员配置";
            if (!policy.Enabled) return "应用守护：未启用";
            var status = GuardJson.ReadFile<GuardStatus>(Path.Combine(Directory, GuardFiles.Status));
            var backup = status is { LastBackupUnixMs: > 0 }
                ? $"，最近备份 {DateTimeOffset.FromUnixTimeMilliseconds(status.LastBackupUnixMs).ToLocalTime():MM-dd HH:mm}"
                : string.Empty;
            return $"应用守护：已由管理员启用（{policy.Describe()}）{backup}";
        }
    }

    public void Start()
    {
        _store.Changed += Apply;
        Apply();
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
                if (policy.Enabled) Deploy(policy);
                else Teardown();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogWarning(ex, "应用守护{Action}失败", policy.Enabled ? "部署" : "清理");
            }
        }
    }

    /// <summary>每分钟：守护进程被结束时重新拉起，并上报守护事件。</summary>
    private void Maintain()
    {
        if (_store.Current is { Enabled: true })
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(GuardExecutablePath) && !_host.IsGuardRunning(GuardExecutablePath))
                    {
                        _logger.LogInformation("应用守护进程未运行，重新启动");
                        _host.StartGuard(GuardExecutablePath, Directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "重新启动应用守护进程失败");
                }
            }
        }
        _ = FlushEventsAsync();
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
        var roots = new List<string> { info.PackageRoot };
        foreach (var root in info.DataRoots)
        {
            if (roots.Any(x => GuardBackup.IsUnder(root, x))) continue;
            roots.Add(root);
        }
        return new GuardConfig
        {
            Enabled = policy.Enabled,
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
            Excludes = info.Excludes.ToList(),
            ClassIslandVersion = info.ClassIslandVersion,
            UpdatedAtUnixMs = nowUnixMs,
        };
    }

    private void Deploy(AppGuardPolicy policy)
    {
        var info = _host.Describe();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = BuildConfig(policy, info, now);
        System.IO.Directory.CreateDirectory(Directory);
        GuardJson.WriteFile(Path.Combine(Directory, GuardFiles.Policy), config);
        WriteState(GuardRunStates.Running);
        if (policy.PreventDeletion && info.IsMsix && !_msixNoticeRecorded)
        {
            _msixNoticeRecorded = true;
            AppendEvent(GuardEventKinds.BackupFailed, "当前 ClassIsland 为 MSIX 安装，不支持禁止删除，其余守护项照常生效");
        }

        var target = GuardExecutablePath;
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
        _host.SetRunKey($"\"{target}\"");
        if (!_host.IsGuardRunning(target)) _host.StartGuard(target, Directory);
    }

    private void Teardown()
    {
        var target = GuardExecutablePath;
        if (File.Exists(target)) _host.StopGuard(target);
        _host.SetRunKey(null);
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
