using System.Diagnostics;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
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
    private readonly AccountMirror? _accounts;
    private readonly PluginSettings? _settings;
    private readonly Stopwatch _stateThrottle = Stopwatch.StartNew();
    private readonly Stopwatch _scheduleThrottle = Stopwatch.StartNew();
    private string? _lastScheduleSignature;
    /// <summary>最近一次成功生成的课表，作为“课表已更新”通知列出具体换课内容的比较基线。</summary>
    private ScheduleBundle? _lastBundle;

    public StateCollector(
        IStateSource stateSource,
        ScheduleCatalog schedules,
        ClassIslandHostControlService hostControl,
        ILogger<StateCollector> logger,
        AccountMirror? accounts = null,
        PluginSettings? settings = null)
    {
        _stateSource = stateSource;
        _schedules = schedules;
        _hostControl = hostControl;
        _logger = logger;
        _accounts = accounts;
        _settings = settings;
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
        var currentItem = _stateSource.CurrentTimeLayoutItem;
        var nextItem = _stateSource.NextClassTimeLayoutItem;
        // ClassIsland 在最后一节课上课期间仍把当堂课报成“下一节”：不晚于当前时段结束的都不算下一节。
        if (currentItem != TimeLayoutItem.Empty && nextItem != TimeLayoutItem.Empty &&
            nextItem.StartTime < currentItem.EndTime)
        {
            nextItem = TimeLayoutItem.Empty;
        }
        var nextSubject = nextItem == TimeLayoutItem.Empty && _stateSource.NextClassTimeLayoutItem != TimeLayoutItem.Empty
            ? null
            : SubjectName(_stateSource.NextClassSubject);
        var hasVolume = _hostControl.TryGetVolumeState(out var volumePercent, out var isMuted);
        return new ClassStateSnapshot
        {
            ScheduleDate = DateTime.Today.ToString("yyyy-MM-dd"),
            CurrentSubject = currentSubject,
            NextClassSubject = nextSubject,
            CurrentState = EffectiveState(),
            CurrentTimeLayoutItem = FormatTimeLayoutItem(currentItem, currentSubject),
            // 随快照带上插件本地时区偏移，手表端据此对齐时间，避免两端时区不一致时进度环为空。
            TimeZoneOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
            NextClassTimeLayoutItem = FormatTimeLayoutItem(nextItem, nextSubject),
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
        var before = _lastBundle;
        PushSchedule(force: true);
        var message = before is null || _lastBundle is null || ReferenceEquals(before, _lastBundle)
            ? null
            : DescribeScheduleChange(before, _lastBundle, DateOnly.FromDateTime(DateTime.Today));
        PushEvent(ClassEventKind.ScheduleChanged, null, message ?? "课表已更新", pushSnapshot: true);
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
            _lastBundle = bundle;
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

    private void LessonsOnOnClass(object? sender, EventArgs e)
    {
        var subject = SubjectName(_stateSource.CurrentSubject);
        PushEvent(ClassEventKind.OnClass, subject,
            DescribeOnClass(ClassLabel(), subject, TimeRange(_stateSource.CurrentTimeLayoutItem)));
    }

    private void LessonsOnOnBreakingTime(object? sender, EventArgs e)
    {
        var next = SubjectName(_stateSource.NextClassSubject);
        PushEvent(ClassEventKind.OnBreaking, next,
            DescribeOnBreaking(ClassLabel(), next, TimeRange(_stateSource.NextClassTimeLayoutItem)));
    }

    /// <summary>通知里展示的班级名：优先服务端下发的归属班级，其次本机填写的班级备注。</summary>
    private string? ClassLabel()
    {
        var name = _accounts?.ClassName;
        if (string.IsNullOrWhiteSpace(name)) name = _settings?.ClassNameRemark;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string? TimeRange(TimeLayoutItem item) =>
        item == TimeLayoutItem.Empty ? null : $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";

    /// <summary>上课通知正文：哪个班上什么课，例如“高一（1）班 数学课（08:00-08:45）”。</summary>
    internal static string DescribeOnClass(string? className, string? subject, string? timeRange)
    {
        var course = subject is null ? "上课了" : $"{subject}课";
        var text = className is null ? course : $"{className} {course}";
        return timeRange is null ? text : $"{text}（{timeRange}）";
    }

    /// <summary>下课通知正文：下节课是什么，例如“高一（1）班 下节课：数学（09:00-09:45）”。</summary>
    internal static string DescribeOnBreaking(string? className, string? nextSubject, string? nextTimeRange)
    {
        var prefix = className is null ? string.Empty : $"{className} ";
        if (nextSubject is null) return $"{prefix}今天没有后续课程了";
        var text = $"{prefix}下节课：{nextSubject}";
        return nextTimeRange is null ? text : $"{text}（{nextTimeRange}）";
    }

    /// <summary>
    /// 比较前后两份课表，列出科目发生变化的节次，例如“今天第 3 节 语文 换为 数学；第 5 节 数学 换为 语文”。
    /// 没有可识别的科目变化时返回 null，由调用方回落为通用文案。
    /// </summary>
    internal static string? DescribeScheduleChange(ScheduleBundle before, ScheduleBundle after, DateOnly today)
    {
        const int maxItems = 6;
        var beforeDays = before.Days.ToDictionary(x => x.Date);
        var parts = new List<string>();
        var total = 0;
        foreach (var day in after.Days)
        {
            if (!beforeDays.TryGetValue(day.Date, out var old)) continue;
            var oldCourses = old.Courses.ToDictionary(x => x.Index);
            var changes = day.Courses
                .Where(x => oldCourses.TryGetValue(x.Index, out var o) && o.Subject != x.Subject)
                .Select(x => $"第 {x.Index + 1} 节 {oldCourses[x.Index].Subject} 换为 {x.Subject}")
                .ToList();
            if (changes.Count == 0) continue;
            total += changes.Count;
            var room = maxItems - parts.Count;
            if (room <= 0) continue;
            changes = changes.Take(room).ToList();
            changes[0] = DayLabel(day.Date, today) + changes[0];
            parts.AddRange(changes);
        }
        if (parts.Count == 0) return null;
        var message = string.Join("；", parts);
        return total > parts.Count ? $"{message} 等 {total} 处" : message;
    }

    private static string DayLabel(string date, DateOnly today)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var value)) return date + " ";
        var days = value.DayNumber - today.DayNumber;
        return days switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => $"{value.Month} 月 {value.Day} 日{WeekdayName(value.DayOfWeek)}",
        };
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "（周一）",
        DayOfWeek.Tuesday => "（周二）",
        DayOfWeek.Wednesday => "（周三）",
        DayOfWeek.Thursday => "（周四）",
        DayOfWeek.Friday => "（周五）",
        DayOfWeek.Saturday => "（周六）",
        _ => "（周日）",
    };

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

    /// <summary>
    /// ClassIsland 的 LessonsService 从不进入 PrepareOnClass，“准备上课”由上课提醒按室内/室外
    /// 准备时长另行判定；这里复刻同一条件，让各端能区分课间与即将上课。
    /// </summary>
    private ClassStateKind EffectiveState()
    {
        var state = _stateSource.CurrentState;
        var left = _stateSource.OnClassLeftTime;
        if (state is TimeState.Breaking or TimeState.None &&
            _stateSource.ClassPreparingDuration is { } preparing &&
            left > TimeSpan.Zero && left <= preparing)
        {
            return ClassStateKind.PrepareClass;
        }
        return MapState(state);
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
