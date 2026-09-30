using System.Diagnostics;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>高频当前状态与低频七日课表分流收集器。</summary>
public sealed class StateCollector
{
    /// <summary>ClassIsland 对未设置科目的占位名称（宿主直接写入字面量）。</summary>
    internal const string UnsetSubjectPlaceholder = "???";

    private readonly IStateSource _stateSource;
    private readonly ScheduleCatalog _schedules;
    private readonly ClassIslandHostControlService _hostControl;
    private readonly ILogger<StateCollector> _logger;
    private readonly Stopwatch _stateThrottle = Stopwatch.StartNew();
    private readonly Stopwatch _scheduleThrottle = Stopwatch.StartNew();
    private string? _lastScheduleSignature;

    public StateCollector(
        IStateSource stateSource,
        ScheduleCatalog schedules,
        ClassIslandHostControlService hostControl,
        ILogger<StateCollector> logger)
    {
        _stateSource = stateSource;
        _schedules = schedules;
        _hostControl = hostControl;
        _logger = logger;
    }

    public event Action<ClassStateSnapshot>? SnapshotPushed;
    public event Action<ScheduleBundle>? SchedulePushed;
    public event Action<string>? SchedulePushFailed;
    public event Action<ClassEvent>? EventOccurred;

    public void Start()
    {
        _stateSource.OnClass += LessonsOnOnClass;
        _stateSource.OnBreakingTime += LessonsOnOnBreakingTime;
        _stateSource.OnAfterSchool += LessonsOnOnAfterSchool;
        _stateSource.CurrentTimeStateChanged += LessonsOnCurrentTimeStateChanged;
        _stateSource.PostMainTimerTicked += LessonsOnPostMainTimerTicked;
        PushSnapshot();
        PushSchedule(force: true);
        _logger.LogInformation("RemoteCI v2 状态与七日课表收集已启动");
    }

    public void Stop()
    {
        _stateSource.OnClass -= LessonsOnOnClass;
        _stateSource.OnBreakingTime -= LessonsOnOnBreakingTime;
        _stateSource.OnAfterSchool -= LessonsOnOnAfterSchool;
        _stateSource.CurrentTimeStateChanged -= LessonsOnCurrentTimeStateChanged;
        _stateSource.PostMainTimerTicked -= LessonsOnPostMainTimerTicked;
    }

    public ClassStateSnapshot BuildSnapshot()
    {
        var currentSubject = SubjectName(_stateSource.CurrentSubject);
        var nextSubject = SubjectName(_stateSource.NextClassSubject);
        var hasVolume = _hostControl.TryGetVolumeState(out var volumePercent, out var isMuted);
        return new ClassStateSnapshot
        {
            ScheduleDate = DateTime.Today.ToString("yyyy-MM-dd"),
            CurrentSubject = currentSubject,
            NextClassSubject = nextSubject,
            CurrentState = MapState(_stateSource.CurrentState),
            CurrentTimeLayoutItem = FormatTimeLayoutItem(_stateSource.CurrentTimeLayoutItem, currentSubject),
            // 随快照带上插件本地时区偏移，手表端据此对齐时间，避免两端时区不一致时进度环为空。
            TimeZoneOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
            NextClassTimeLayoutItem = FormatTimeLayoutItem(_stateSource.NextClassTimeLayoutItem, nextSubject),
            ClassPlanName = _stateSource.CurrentClassPlan?.Name,
            IsClassPlanEnabled = _stateSource.IsClassPlanEnabled,
            IsClassPlanLoaded = _stateSource.IsClassPlanLoaded,
            OnClassLeftTime = _stateSource.OnClassLeftTime,
            OnBreakingLeftTime = _stateSource.OnBreakingTimeLeftTime,
            LessonConfirmed = _stateSource.IsLessonConfirmed,
            IsNotificationPlaying = _hostControl.IsNotificationPlaying,
            IsMainMenuVisible = _hostControl.IsMainMenuVisible,
            IsSleepAvailable = _hostControl.IsSleepAvailable,
            IsHibernateAvailable = _hostControl.IsHibernateAvailable,
            IsVolumeControlAvailable = hasVolume,
            VolumePercent = volumePercent,
            IsMuted = isMuted,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    public ScheduleBundle BuildSchedule() => _schedules.BuildBundle();

    public void ForceSchedulePush()
    {
        PushSchedule(force: true);
        PushEvent(ClassEventKind.ScheduleChanged, null, "课表已更新", pushSnapshot: true);
    }

    /// <summary>响应远端拉取，仅重新生成并推送课表，不伪造“课表已变更”事件。</summary>
    public void RequestSchedulePush() => PushSchedule(force: true);

    public void ForceSnapshotPush() => PushSnapshot();

    private void PushSnapshot()
    {
        // 该链每秒在 ClassIsland 主计时器（UI 线程）上执行，任何异常都不允许逃逸到宿主。
        try
        {
            SnapshotPushed?.Invoke(BuildSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成或推送状态快照失败");
        }
    }

    private void PushSchedule(bool force)
    {
        try
        {
            var bundle = BuildSchedule();
            // 修订号只覆盖课程结构；科目改名与课表名变化不改变修订号，必须纳入签名才会重新推送。
            var signature = BuildScheduleSignature(bundle);
            if (!force && signature == _lastScheduleSignature) return;
            _lastScheduleSignature = signature;
            SchedulePushed?.Invoke(bundle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成或推送七日课表失败");
            SchedulePushFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>低频推送的变更签名：修订号 + 课表名 + 全部科目名，任一项变化都触发重推。</summary>
    internal static string BuildScheduleSignature(ScheduleBundle bundle) =>
        string.Join('|', bundle.Days.Select(x => $"{x.Date}:{x.Revision}:{x.ClassPlanName}"))
        + "|subjects:" + string.Join(',', bundle.Subjects.Select(x => x.Name));

    private void PushEvent(ClassEventKind kind, string? subject, string message, bool pushSnapshot = true)
    {
        try
        {
            EventOccurred?.Invoke(new ClassEvent { Event = kind, Subject = subject, Message = message });
            if (pushSnapshot) PushSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推送课程事件失败");
        }
    }

    private void LessonsOnOnClass(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnClass, SubjectName(_stateSource.CurrentSubject), $"上课了：{SubjectName(_stateSource.CurrentSubject)}");

    private void LessonsOnOnBreakingTime(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnBreaking, null, "下课休息");

    private void LessonsOnOnAfterSchool(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnAfterSchool, null, "放学啦！");

    private void LessonsOnCurrentTimeStateChanged(object? sender, EventArgs e) => PushSnapshot();

    private void LessonsOnPostMainTimerTicked(object? sender, EventArgs e)
    {
        if (_stateThrottle.ElapsedMilliseconds >= 1000)
        {
            _stateThrottle.Restart();
            PushSnapshot();
        }
        if (_scheduleThrottle.Elapsed >= TimeSpan.FromSeconds(30))
        {
            _scheduleThrottle.Restart();
            PushSchedule(force: false);
        }
    }

    internal static ClassStateKind MapState(TimeState state) => state switch
    {
        TimeState.OnClass => ClassStateKind.Class,
        TimeState.PrepareOnClass => ClassStateKind.PrepareClass,
        TimeState.Breaking => ClassStateKind.Breaking,
        TimeState.AfterSchool => ClassStateKind.AfterSchool,
        _ => ClassStateKind.None,
    };

    internal static string? SubjectName(Subject? subject) => subject?.Name switch
    {
        null or "" or UnsetSubjectPlaceholder => null,
        var name => name,
    };

    internal static string FormatTimeLayoutItem(TimeLayoutItem item, string? subjectName)
    {
        if (item == TimeLayoutItem.Empty) return string.Empty;
        var time = $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";
        return item.TimeType switch
        {
            0 => subjectName is null ? time : $"{time} {subjectName}",
            1 => $"{time} {item.BreakNameText}",
            _ => $"{time} {item}",
        };
    }
}
