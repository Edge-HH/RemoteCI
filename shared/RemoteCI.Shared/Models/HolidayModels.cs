using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>服务端下发给插件的调休日历：只含窗口内的放假日与调休上学日，补课周几已按覆盖规则算好。</summary>
public sealed class HolidayCalendar
{
    /// <summary>为 false 时 Days 为空，插件应撤销之前做过的全部调整。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("days")]
    public List<HolidayCalendarDay> Days { get; set; } = [];
}

public sealed class HolidayCalendarDay
{
    /// <summary>yyyy-MM-dd。</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary><see cref="HolidayDayKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>调休上学日补的工作日（1=周一 … 5=周五）；null 表示不补课或无法推算。</summary>
    [JsonPropertyName("followWeekday")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FollowWeekday { get; set; }

    /// <summary><see cref="HolidayFollowSources"/>；仅调休上学日有值。</summary>
    [JsonPropertyName("followSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FollowSource { get; set; }
}

public static class HolidayDayKinds
{
    public const string Off = "off";
    public const string Makeup = "makeup";
}

public static class HolidayFollowSources
{
    public const string Auto = "auto";
    public const string Manual = "manual";
    public const string Skip = "skip";
    public const string Unresolved = "unresolved";
}

/// <summary>插件上报课表时给特殊日期打的标记（<see cref="ScheduleDay.DayKind"/>）。</summary>
public static class ScheduleDayKinds
{
    public const string Holiday = "holiday";
    public const string Makeup = "makeup";
}
