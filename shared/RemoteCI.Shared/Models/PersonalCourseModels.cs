using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 仅属于当前账号的自定义课程：每周固定星期几的固定时段，可设单双周或多周轮换（规则同档案课表 TimeRule）。
/// 轮换按管理员设置的学期开始日期计算教学周。未提供 weekDay 而提供 startsAt/endsAt 时创建只在该日出现的单次课程（兼容旧客户端）。
/// </summary>
public class PersonalCourseRequest
{
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;
    /// <summary>星期几：0=周日，1=周一 … 6=周六（与 ClassIsland TimeRule.WeekDay 相同）。</summary>
    [JsonPropertyName("weekDay")]
    public int? WeekDay { get; set; }
    /// <summary>开始时刻 HH:mm（课程所在时区的本地时间）。</summary>
    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }
    /// <summary>结束时刻 HH:mm，需晚于开始时刻且在同一天内。</summary>
    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }
    /// <summary>0 表示每周；否则为 weekCountDivTotal 周一轮中的第几周（单周=1/2，双周=2/2）。</summary>
    [JsonPropertyName("weekCountDiv")]
    public int WeekCountDiv { get; set; }
    /// <summary>轮换总周数 2–4；weekCountDiv 为 0 时忽略。</summary>
    [JsonPropertyName("weekCountDivTotal")]
    public int WeekCountDivTotal { get; set; } = 2;
    /// <summary>课程时刻所在时区相对 UTC 的分钟数；省略时使用服务端时区。</summary>
    [JsonPropertyName("timeZoneOffsetMinutes")]
    public int? TimeZoneOffsetMinutes { get; set; }
    /// <summary>单次课程的开始时间（含 UTC 偏移），仅在未提供 weekDay 时使用。</summary>
    [JsonPropertyName("startsAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartsAt { get; set; }
    /// <summary>单次课程的结束时间，仅在未提供 weekDay 时使用。</summary>
    [JsonPropertyName("endsAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndsAt { get; set; }
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
    /// <summary>单次课程所在日期（yyyy-MM-dd）；每周重复的课程省略。</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }
    /// <summary>重复规则的中文说明，例如“每周一 08:00–08:45 · 单周”。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}
