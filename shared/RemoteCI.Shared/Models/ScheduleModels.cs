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

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class SubjectEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
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
}
