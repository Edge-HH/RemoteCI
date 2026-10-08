using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

public sealed class ScheduleSyncRequest
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("source")]
    public ScheduleSyncSource Source { get; set; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>任务目标班级；缺省表示单班级部署的默认班级。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    public static ScheduleSyncRequest Create(ScheduleSyncSource source, string? taskId = null) => new()
    {
        TaskId = string.IsNullOrWhiteSpace(taskId) ? Guid.NewGuid().ToString("N") : taskId,
        Source = source,
    };
}

public sealed class ScheduleSyncStatus
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public ScheduleSyncSource Source { get; set; }

    [JsonPropertyName("state")]
    public ScheduleSyncTaskState State { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("finishedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Busy 状态下指向当前正在执行的任务。</summary>
    [JsonPropertyName("activeTaskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveTaskId { get; set; }

    /// <summary>任务归属班级；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }
}

public sealed class ScheduleBundle
{
    [JsonPropertyName("fromDate")]
    public string FromDate { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>课表归属班级；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    [JsonPropertyName("days")]
    public List<ScheduleDay> Days { get; set; } = [];

    [JsonPropertyName("subjects")]
    public List<SubjectEntry> Subjects { get; set; } = [];
}

public sealed class ScheduleDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("classPlanName")]
    public string? ClassPlanName { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("courses")]
    public List<CourseEntry> Courses { get; set; } = [];

    /// <summary>放假日为 "holiday"、调休上学日为 "makeup"，普通日不输出；旧版插件不下发。</summary>
    [JsonPropertyName("dayKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DayKind { get; set; }

    /// <summary>放假日或调休上学日所属的假期名，例如“国庆节”。</summary>
    [JsonPropertyName("holidayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HolidayName { get; set; }
}

public sealed class CourseEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("subjectId")]
    public Guid SubjectId { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }

    /// <summary>该科目授课教师名，来自 ClassIsland 档案的 Subject.TeacherName；旧版插件不下发。</summary>
    [JsonPropertyName("teacher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Teacher { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class SubjectEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>授课教师名，来自 ClassIsland 档案的 Subject.TeacherName；旧版插件不下发。</summary>
    [JsonPropertyName("teacher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Teacher { get; set; }
}

/// <summary>SetSubjectTeacher 命令参数：teacherName 为空表示清除该科目的教师。</summary>
public sealed class SubjectTeacherRequest
{
    /// <summary>教师名长度上限；服务端与插件共用同一校验。</summary>
    public const int MaxTeacherNameLength = 100;

    [JsonPropertyName("subjectId")]
    public Guid SubjectId { get; set; }

    [JsonPropertyName("teacherName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeacherName { get; set; }
}

public sealed class ScheduleChangeRequest
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public ScheduleChangeMode Mode { get; set; }

    [JsonPropertyName("sourceIndex")]
    public int SourceIndex { get; set; }

    [JsonPropertyName("targetIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetIndex { get; set; }

    [JsonPropertyName("replacementSubjectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReplacementSubjectId { get; set; }

    [JsonPropertyName("expectedRevision")]
    public string ExpectedRevision { get; set; } = string.Empty;

    /// <summary>写入 ClassIsland 源课表，使本周及以后每周持续生效。</summary>
    [JsonPropertyName("permanent")]
    public bool Permanent { get; set; }
}

/// <summary>“我的日程”响应：把当前用户任教班级的课表按日期聚合。</summary>
public sealed class MyScheduleResponse
{
    [JsonPropertyName("fromDate")]
    public string FromDate { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("days")]
    public List<MyScheduleDay> Days { get; set; } = [];
}

public sealed class MyScheduleDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<MyScheduleItem> Items { get; set; } = [];
}

/// <summary>某一天中用户在某个班级的课程集合。</summary>
public sealed class MyScheduleItem
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("courses")]
    public List<CourseEntry> Courses { get; set; } = [];
}

/// <summary>“我的日程”中的一节课：所在班级、课程以及换算成绝对时间的起止时刻。</summary>
public sealed class MyCourseSlot
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("course")]
    public CourseEntry Course { get; set; } = new();

    [JsonPropertyName("startsAt")]
    public DateTimeOffset StartsAt { get; set; }

    [JsonPropertyName("endsAt")]
    public DateTimeOffset EndsAt { get; set; }
}

/// <summary>“下一节课”响应：指定时刻正在上的课与接下来的第一节课；没有时对应字段省略。</summary>
public sealed class MyNextCourseResponse
{
    /// <summary>计算所依据的时刻；请求未指定 at 时为服务端当前时间。</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("current")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MyCourseSlot? Current { get; set; }

    [JsonPropertyName("next")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MyCourseSlot? Next { get; set; }
}

public sealed class NotificationRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>服务端根据全局“强制在标题显示发送人”设置注入；null 时插件按旧行为视为开启。</summary>
    [JsonPropertyName("forceSenderInTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForceSenderInTitle { get; set; }

    [JsonPropertyName("isNotificationEffectEnabled")]
    public bool IsNotificationEffectEnabled { get; set; }

    [JsonPropertyName("isNotificationSoundEnabled")]
    public bool IsNotificationSoundEnabled { get; set; }

    [JsonPropertyName("isSpeechEnabled")]
    public bool IsSpeechEnabled { get; set; }

    /// <summary>是否在提醒时置顶 ClassIsland 主界面（对齐 ClassIsland 集控的 IsTopmost）。</summary>
    [JsonPropertyName("isNotificationTopmostEnabled")]
    public bool IsNotificationTopmostEnabled { get; set; }

    /// <summary>单条提醒的显示秒数；null 或 0 时按 ClassIsland 集控默认 5 秒处理，上限 <see cref="MaxDurationSeconds"/>。</summary>
    [JsonPropertyName("durationSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationSeconds { get; set; }

    /// <summary>重复次数；null 或 0 时按 1 次处理（对齐 ClassIsland 集控的 RepeatCounts），上限 <see cref="MaxRepeatCounts"/>。
    /// 滚动时正文滚动 N 遍；静态时整条提醒依次显示 N 次。</summary>
    [JsonPropertyName("repeatCounts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RepeatCounts { get; set; }

    /// <summary>正文是否以横向滚动文本显示。三态：省略（旧 V3 客户端）保持升级前的滚动行为，
    /// 显式 <c>false</c> 才静态显示正文，显式 <c>true</c> 滚动显示。</summary>
    [JsonPropertyName("isRollingEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsRollingEnabled { get; set; }

    /// <summary>把“持续时间（秒）”和“重复次数”归一化为插件可直接使用的取值，并限幅到协议上限。</summary>
    public int EffectiveDurationSeconds => DurationSeconds is null or <= 0
        ? DefaultDurationSeconds
        : Math.Min(DurationSeconds.Value, MaxDurationSeconds);

    public int EffectiveRepeatCounts => RepeatCounts is null or < 1 ? 1 : Math.Min(RepeatCounts.Value, MaxRepeatCounts);

    /// <summary>省略字段的旧客户端沿用升级前的滚动正文。</summary>
    public bool EffectiveRollingEnabled => IsRollingEnabled ?? true;

    /// <summary>
    /// 所有入口（WebSocket 命令、REST、集控广播、WebUI）共用的通知参数校验；通过返回 null，否则返回面向用户的错误说明。
    /// 0 与 null 一样表示默认值，以兼容旧客户端；负数或超过上限一律拒绝，防止大量通知排队。
    /// 标题沿用旧行为在执行端截断到 <see cref="MaxTitleLength"/> 字，不作为拒绝条件。
    /// </summary>
    public static string? Validate(NotificationRequest? request)
    {
        if (request is null) return "缺少通知内容";
        if ((request.Message?.Trim().Length ?? 0) > MaxMessageLength) return $"通知正文不能超过 {MaxMessageLength} 个字符";
        if (request.DurationSeconds is < 0 or > MaxDurationSeconds) return $"持续时间必须在 1-{MaxDurationSeconds} 秒之间";
        if (request.RepeatCounts is < 0 or > MaxRepeatCounts) return $"重复次数必须在 1-{MaxRepeatCounts} 次之间";
        return null;
    }

    /// <summary>与 ClassIsland 集控 SendNotification 一致的默认显示秒数。</summary>
    public const int DefaultDurationSeconds = 5;

    public const int MaxTitleLength = 60;
    public const int MaxMessageLength = 500;
    public const int MaxDurationSeconds = 3600;
    public const int MaxRepeatCounts = 10;

    /// <summary>正文超过该字数且未开启滚动时，各端发送界面提示建议开启滚动。</summary>
    public const int RollingSuggestionThreshold = 30;
}
