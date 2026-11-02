using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>按周浏览课表时的一个节次行：左侧显示的时间取本周出现最多的时间表（见 <see cref="ScheduleTimeAxis"/>）。</summary>
public sealed class SchedulePeriod
{
    /// <summary>节次（零基），与 <see cref="CourseEntry.Index"/> 一致。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("startTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTime { get; set; }

    /// <summary>与上一节之间空出至少 <see cref="ScheduleTimeAxis.GapMinutes"/> 分钟：表格在两节之间留出空白。</summary>
    [JsonPropertyName("gapBefore")]
    public bool GapBefore { get; set; }

    /// <summary>时间表在上一节与本节之间有分割线：表格画一条不带名字的细线。</summary>
    [JsonPropertyName("dividerBefore")]
    public bool DividerBefore { get; set; }
}

/// <summary>GET /api/schedule/week 的响应：某班一周（或指定日期范围）的生效课表。</summary>
public sealed class ScheduleWeekResponse
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    /// <summary>请求范围的首日与末日（yyyy-MM-dd）。</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>该班教室端本地的今天。</summary>
    [JsonPropertyName("today")]
    public string Today { get; set; } = string.Empty;

    /// <summary>可浏览的最早日期：学期开始所在周的周一，未设置学期时为本周一。</summary>
    [JsonPropertyName("minDate")]
    public string MinDate { get; set; } = string.Empty;

    /// <summary>有课表数据的最后一天；之后的日期没有数据。</summary>
    [JsonPropertyName("maxDate")]
    public string MaxDate { get; set; } = string.Empty;

    /// <summary>范围首日所在的教学周（从 1 开始）；未设置学期或开学前省略。</summary>
    [JsonPropertyName("teachingWeek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TeachingWeek { get; set; }

    /// <summary>"profile" 表示今天及以后的课按服务端班级档案推算（插件从未同步过课表）。</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("periods")]
    public List<SchedulePeriod> Periods { get; set; } = [];

    /// <summary>范围内有数据的日期（超出 minDate–maxDate 的日期不输出）；已过去的日子来自课时台账。</summary>
    [JsonPropertyName("days")]
    public List<ScheduleDay> Days { get; set; } = [];

    [JsonPropertyName("subjects")]
    public List<SubjectEntry> Subjects { get; set; } = [];
}

/// <summary>GET /api/me/schedule/week 的响应：当前用户“我的日程”在指定日期范围内的课。</summary>
public sealed class MyScheduleWeekResponse
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("today")]
    public string Today { get; set; } = string.Empty;

    [JsonPropertyName("minDate")]
    public string MinDate { get; set; } = string.Empty;

    [JsonPropertyName("maxDate")]
    public string MaxDate { get; set; } = string.Empty;

    [JsonPropertyName("teachingWeek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TeachingWeek { get; set; }

    [JsonPropertyName("periods")]
    public List<SchedulePeriod> Periods { get; set; } = [];

    [JsonPropertyName("days")]
    public List<MyScheduleDay> Days { get; set; } = [];
}

/// <summary>GET /api/me/subject-colors 的响应：色板与当前用户可见学科的颜色（已按拼音排序）。</summary>
public sealed class SubjectColorsResponse
{
    /// <summary>可选颜色（#RRGGBB），即 <see cref="SubjectColors.Palette"/>。</summary>
    [JsonPropertyName("palette")]
    public List<string> Palette { get; set; } = [];

    [JsonPropertyName("items")]
    public List<SubjectColorItem> Items { get; set; } = [];
}

public sealed class SubjectColorItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>生效颜色：用户改过的颜色，否则为 <see cref="SubjectColors.Default"/>。</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    /// <summary>用户改过颜色。</summary>
    [JsonPropertyName("custom")]
    public bool Custom { get; set; }
}

/// <summary>PUT /api/me/subject-colors 的请求体。</summary>
public sealed class SubjectColorRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;
}
