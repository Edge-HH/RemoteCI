using System;
using System.Collections.Generic;

namespace RemoteCI.Guard;

/// <summary>守护事件类型，取值必须与 RemoteCI.Shared 的 AppGuardEventKinds 一致（由插件测试校验）。</summary>
internal static class GuardEventKinds
{
    public const string Restored = "restored";
    public const string RestoreFailed = "restore-failed";
    public const string CrashRestarted = "crash-restarted";
    public const string ExitRestarted = "exit-restarted";
    public const string CrashLoopPaused = "crash-loop-paused";
    public const string BackupCompleted = "backup-completed";
    public const string BackupFailed = "backup-failed";
}

internal enum GuardAction
{
    None,
    Launch,
    Restore,
}

internal sealed class GuardDecision
{
    public static readonly GuardDecision None = new(GuardAction.None, null, null);

    public GuardDecision(GuardAction action, string? eventKind, string? detail)
    {
        Action = action;
        EventKind = eventKind;
        Detail = detail;
    }

    public GuardAction Action { get; }

    /// <summary>执行成功后要记录的事件；为 null 时只写日志。</summary>
    public string? EventKind { get; }

    public string? Detail { get; }
}

/// <summary>一次轮询看到的情况。</summary>
internal sealed class GuardObservation
{
    public long NowMs { get; set; }

    /// <summary>安装目录下有任何进程在运行（ClassIsland 本体、启动器或自带更新器）。</summary>
    public bool Running { get; set; }

    /// <summary>启动器与本体程序都还在。</summary>
    public bool InstallPresent { get; set; }

    /// <summary>插件最后写入的运行标记；没有标记文件时为 null。</summary>
    public string? RunState { get; set; }

    public long RunStateAtMs { get; set; }
}

/// <summary>
/// 守护逻辑（纯函数，不碰进程与文件，便于单测）：
/// 正常退出（插件写了 exiting）→ 按延时启动；没有退出标记就消失 → 视为意外退出，5 秒后重启，
/// 10 分钟内超过 3 次则暂停 10 分钟；升级中（updating）最多等 15 分钟不干预；
/// 安装目录缺失且 10 秒后复查仍缺失 → 从备份恢复。任何时候只要 ClassIsland 又出现就取消待执行的启动。
/// </summary>
internal sealed class GuardStateMachine
{
    public const long CrashRestartDelayMs = 5_000;
    public const long MinExitRestartDelayMs = 10_000;
    public const long DeletionConfirmMs = 10_000;
    public const long StartupGraceMs = 60_000;
    public const long LaunchConfirmMs = 120_000;
    public const long UpdatingWaitMs = 15 * 60_000;
    public const long CrashWindowMs = 10 * 60_000;
    public const int CrashLimit = 3;
    public const long CrashPauseMs = 10 * 60_000;
    public const long RestoreRetryMs = 5 * 60_000;
    public const long LaunchRetryMs = 60_000;

    private bool? _wasRunning;
    private long? _pendingLaunchAt;
    private string? _pendingEvent;
    private string? _pendingDetail;
    private long _updatingUntil;
    private long _pausedUntil;
    private long? _missingSince;
    private long _restoreBlockedUntil;
    private long? _expectStartUntil;
    private readonly List<long> _crashes = new();

    public bool HasPendingLaunch => _pendingLaunchAt is not null;

    public long? PendingLaunchAt => _pendingLaunchAt;

    public GuardDecision Tick(GuardObservation o, GuardConfig policy)
    {
        if (o.Running)
        {
            _wasRunning = true;
            _pendingLaunchAt = null;
            _pendingEvent = null;
            _pendingDetail = null;
            _missingSince = null;
            _expectStartUntil = null;
            _updatingUntil = 0;
            return GuardDecision.None;
        }

        if (!o.InstallPresent)
        {
            if (!policy.PreventDeletion || o.NowMs < _restoreBlockedUntil) return GuardDecision.None;
            if (_missingSince is not { } since)
            {
                _missingSince = o.NowMs;
                return GuardDecision.None;
            }
            if (o.NowMs - since < DeletionConfirmMs) return GuardDecision.None;
            _missingSince = null;
            _wasRunning = false;
            _pendingLaunchAt = null;
            _expectStartUntil = o.NowMs + LaunchConfirmMs;
            return new GuardDecision(GuardAction.Restore, GuardEventKinds.Restored, null);
        }
        _missingSince = null;

        if (_wasRunning is null)
        {
            // 守护进程刚启动（通常是登录 Windows 后）：给 ClassIsland 自己的开机自启留一分钟，仍未运行再拉起。
            _wasRunning = false;
            if (policy.RestartAfterExit || policy.RestartOnCrash)
                Schedule(o.NowMs + StartupGraceMs, null, "登录后启动");
            return GuardDecision.None;
        }

        if (_wasRunning == true)
        {
            _wasRunning = false;
            var decision = OnExited(o, policy);
            if (decision.Action != GuardAction.None || decision.EventKind is not null) return decision;
        }
        else if (_expectStartUntil is { } expect && o.NowMs >= expect)
        {
            // 拉起后两分钟仍没有运行，按一次意外退出计入崩溃次数，避免卡在“已启动但没起来”。
            _expectStartUntil = null;
            var decision = OnCrashed(o.NowMs, policy, "启动后未能保持运行");
            if (decision.EventKind is not null) return decision;
        }

        if (o.NowMs < _updatingUntil) return GuardDecision.None;
        if (_pendingLaunchAt is { } due && o.NowMs >= due && o.NowMs >= _pausedUntil)
        {
            var decision = new GuardDecision(GuardAction.Launch, _pendingEvent, _pendingDetail);
            _pendingLaunchAt = null;
            _pendingEvent = null;
            _pendingDetail = null;
            _expectStartUntil = o.NowMs + LaunchConfirmMs;
            return decision;
        }
        return GuardDecision.None;
    }

    /// <summary>恢复失败后 5 分钟内不再尝试，避免反复复制占满磁盘。</summary>
    public void RestoreFailed(long nowMs)
    {
        _restoreBlockedUntil = nowMs + RestoreRetryMs;
        _expectStartUntil = null;
    }

    /// <summary>启动程序失败（例如文件被占用）时一分钟后重试。</summary>
    public void LaunchFailed(long nowMs, string? eventKind, string? detail)
    {
        _expectStartUntil = null;
        Schedule(nowMs + LaunchRetryMs, eventKind, detail);
    }

    private GuardDecision OnExited(GuardObservation o, GuardConfig policy)
    {
        switch (o.RunState)
        {
            case GuardRunStates.Updating when o.NowMs - o.RunStateAtMs < UpdatingWaitMs:
                _updatingUntil = o.RunStateAtMs + UpdatingWaitMs;
                if (policy.RestartAfterExit || policy.RestartOnCrash)
                    Schedule(_updatingUntil, null, "升级等待结束后启动");
                return GuardDecision.None;
            case GuardRunStates.Exiting:
                if (!policy.RestartAfterExit) return GuardDecision.None;
                var delay = Math.Max(MinExitRestartDelayMs, Math.Max(0, policy.RestartDelayMinutes) * 60_000L);
                var detail = policy.RestartDelayMinutes > 0 ? $"退出 {policy.RestartDelayMinutes} 分钟后自动启动" : "退出后自动启动";
                Schedule(o.NowMs + delay, GuardEventKinds.ExitRestarted, detail);
                return GuardDecision.None;
            default:
                return OnCrashed(o.NowMs, policy, "未收到正常退出标记");
        }
    }

    private GuardDecision OnCrashed(long nowMs, GuardConfig policy, string detail)
    {
        if (!policy.RestartOnCrash) return GuardDecision.None;
        _crashes.RemoveAll(x => nowMs - x > CrashWindowMs);
        if (_crashes.Count >= CrashLimit)
        {
            _crashes.Clear();
            _pausedUntil = nowMs + CrashPauseMs;
            Schedule(_pausedUntil, GuardEventKinds.CrashRestarted, "频繁崩溃暂停结束后重启");
            return new GuardDecision(GuardAction.None, GuardEventKinds.CrashLoopPaused,
                $"{CrashWindowMs / 60_000} 分钟内意外退出超过 {CrashLimit} 次，暂停自动重启 {CrashPauseMs / 60_000} 分钟");
        }
        _crashes.Add(nowMs);
        Schedule(nowMs + CrashRestartDelayMs, GuardEventKinds.CrashRestarted, detail);
        return GuardDecision.None;
    }

    private void Schedule(long at, string? eventKind, string? detail)
    {
        _pendingLaunchAt = at;
        _pendingEvent = eventKind;
        _pendingDetail = detail;
    }
}
