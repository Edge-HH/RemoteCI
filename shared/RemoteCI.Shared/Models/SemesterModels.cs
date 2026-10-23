using System.Globalization;
using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>系统管理员统一设置的学期信息；教学周从学期开始日期所在周的周一算起。</summary>
public sealed class SemesterSettings
{
    /// <summary>学期开始日期（yyyy-MM-dd）；null 表示未设置。</summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    /// <summary>是否把学期开始日期写入教室端 ClassIsland 的“单周起始日期”，使多周轮换与服务端一致。</summary>
    [JsonPropertyName("syncToClassrooms")]
    public bool SyncToClassrooms { get; set; } = true;
}

/// <summary>GET /api/settings/semester 的响应：在设置之外附带按服务端今天计算的教学周。</summary>
public sealed class SemesterSettingsView
{
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("syncToClassrooms")]
    public bool SyncToClassrooms { get; set; } = true;

    /// <summary>今天是第几教学周（从 1 开始）；未设置或学期尚未开始时省略。</summary>
    [JsonPropertyName("currentWeek")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentWeek { get; set; }
}

/// <summary>服务端下发给插件的学期设置（<see cref="Protocol.MessageTypeSemester"/>）。</summary>
public sealed class SemesterSync
{
    /// <summary>第 1 教学周的周一（yyyy-MM-dd），写入 ClassIsland Settings.SingleWeekStartTime。</summary>
    [JsonPropertyName("weekOneMonday")]
    public string WeekOneMonday { get; set; } = string.Empty;
}

/// <summary>教学周与多周轮换的计算，规则与 ClassIsland 课表 TimeRule（WeekCountDiv / WeekCountDivTotal）一致。</summary>
public static class SemesterWeeks
{
    /// <summary>多周轮换最多支持的周数，与 ClassIsland 默认的 MultiWeekRotationMaxCycle 一致。</summary>
    public const int MaxRotation = 4;

    public static DateOnly? Parse(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>日期所在周的周一。</summary>
    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>距第 1 教学周的周数（第 1 周为 0，开学前为负数）。</summary>
    public static int WeekIndex(DateOnly semesterStart, DateOnly date) =>
        (int)Math.Floor((date.DayNumber - Monday(semesterStart).DayNumber) / 7.0);

    /// <summary>教学周序号（从 1 开始）；开学前返回 null。</summary>
    public static int? TeachingWeek(DateOnly semesterStart, DateOnly date) =>
        WeekIndex(semesterStart, date) is var index and >= 0 ? index + 1 : null;

    /// <summary>
    /// 该周是否在轮换规则内：<paramref name="div"/> 为 0 表示每周；否则表示 <paramref name="total"/> 周一轮中的第几周。
    /// 例如 div=1,total=2 为单周（第 1、3、5… 教学周），div=2,total=2 为双周。
    /// </summary>
    public static bool InRotation(int div, int total, int weekIndex)
    {
        if (div <= 0) return true;
        total = Math.Max(1, total);
        return div == ((weekIndex % total) + total) % total + 1;
    }

    public static bool IsValidRotation(int div, int total) =>
        div == 0 ? total is >= 1 and <= 99 : total is >= 2 and <= MaxRotation && div >= 1 && div <= total;

    public static string RotationLabel(int div, int total) => div <= 0 ? "每周" : total == 2
        ? (div == 1 ? "单周" : "双周")
        : $"{total} 周轮换第 {div} 周";

    public static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
}
