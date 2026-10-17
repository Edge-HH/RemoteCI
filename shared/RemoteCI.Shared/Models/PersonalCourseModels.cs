using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>仅属于当前账号的单次自定义课程；时间必须包含 UTC 偏移。</summary>
public class PersonalCourseRequest
{
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;
    [JsonPropertyName("startsAt")]
    public DateTimeOffset StartsAt { get; set; }
    [JsonPropertyName("endsAt")]
    public DateTimeOffset EndsAt { get; set; }
    /// <summary>null 关闭课前提醒；1–10080 为提前分钟数。</summary>
    [JsonPropertyName("reminderMinutes")]
    public int? ReminderMinutes { get; set; }
    [JsonPropertyName("notifyAtStart")]
    public bool NotifyAtStart { get; set; } = true;
}

public sealed class PersonalCourseView : PersonalCourseRequest
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
}
