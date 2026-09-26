using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 课程事件（event_notify 载荷），手表据此触发通知+振动。
/// </summary>
public sealed class ClassEvent
{
    /// <summary>事件归属班级；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    /// <summary>跨重连去重使用的稳定事件标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("event")]
    public ClassEventKind Event { get; set; }

    /// <summary>事件关联科目（上课/课间事件有值）。</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>面向用户的提示文案（插件侧生成，中文）。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>事件发生时间（UTC）。</summary>
    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
