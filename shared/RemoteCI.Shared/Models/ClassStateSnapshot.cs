using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>高频状态快照，不包含低频七日课表。</summary>
public sealed class ClassStateSnapshot
{
    /// <summary>快照归属班级；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    [JsonPropertyName("scheduleDate")]
    public string? ScheduleDate { get; set; }

    [JsonPropertyName("currentSubject")]
    public string? CurrentSubject { get; set; }

    [JsonPropertyName("nextClassSubject")]
    public string? NextClassSubject { get; set; }

    [JsonPropertyName("currentState")]
    public ClassStateKind CurrentState { get; set; }

    [JsonPropertyName("currentTimeLayoutItem")]
    public string? CurrentTimeLayoutItem { get; set; }

    /// <summary>
    /// 插件本地时区相对 UTC 的偏移分钟数（如东八区为 480）。
    /// 手表端据此把 UTC 时间戳换算为插件本地时间，避免两端时区不一致时课程进度计算错误。
    /// </summary>
    [JsonPropertyName("timeZoneOffsetMinutes")]
    public int? TimeZoneOffsetMinutes { get; set; }

    [JsonPropertyName("nextClassTimeLayoutItem")]
    public string? NextClassTimeLayoutItem { get; set; }

    [JsonPropertyName("classPlanName")]
    public string? ClassPlanName { get; set; }

    [JsonPropertyName("isClassPlanEnabled")]
    public bool IsClassPlanEnabled { get; set; }

    [JsonPropertyName("isClassPlanLoaded")]
    public bool IsClassPlanLoaded { get; set; }

    [JsonPropertyName("onClassLeftTime")]
    public TimeSpan? OnClassLeftTime { get; set; }

    [JsonPropertyName("onBreakingLeftTime")]
    public TimeSpan? OnBreakingLeftTime { get; set; }

    [JsonPropertyName("lessonConfirmed")]
    public bool LessonConfirmed { get; set; }

    [JsonPropertyName("isNotificationPlaying")]
    public bool IsNotificationPlaying { get; set; }

    [JsonPropertyName("isMainMenuVisible")]
    public bool IsMainMenuVisible { get; set; } = true;

    [JsonPropertyName("isSleepAvailable")]
    public bool IsSleepAvailable { get; set; }

    [JsonPropertyName("isHibernateAvailable")]
    public bool IsHibernateAvailable { get; set; }

    [JsonPropertyName("isVolumeControlAvailable")]
    public bool IsVolumeControlAvailable { get; set; }

    [JsonPropertyName("volumePercent")]
    public int VolumePercent { get; set; }

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
