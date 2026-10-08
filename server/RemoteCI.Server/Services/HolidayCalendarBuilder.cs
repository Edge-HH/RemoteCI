using System.Globalization;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>管理员对某个调休上学日的手动安排；FollowWeekday 为 null 表示不补课。</summary>
public sealed record HolidayOverride(DateOnly Date, int? FollowWeekday);

/// <summary>叠加推算与覆盖后的一天；AutoWeekday 始终是自动推算值，便于页面同时展示两者。</summary>
public sealed record ResolvedHolidayDay(
    int Year, DateOnly Date, string Kind, string Name, int? AutoWeekday, int? FollowWeekday, string? FollowSource);

public static class HolidayCalendarBuilder
{
    public const int WindowPastDays = 1;
    public const int WindowFutureDays = 60;

    /// <summary>1=周一 … 7=周日。</summary>
    public static int IsoWeekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    /// <summary>
    /// 同一年度文件中按假期名分组：调休上学日按日期升序，依次对应该假期最后 N 个落在工作日的放假日，
    /// 补那一天的星期。工作日放假日不够配对时标为 unresolved，交给管理员手动指定。
    /// </summary>
    public static IReadOnlyList<ResolvedHolidayDay> Resolve(
        IReadOnlyDictionary<int, IReadOnlyList<HolidayEntry>> years,
        IReadOnlyCollection<HolidayOverride> overrides)
    {
        var manual = overrides.ToDictionary(x => x.Date);
        var seen = new HashSet<DateOnly>();
        var result = new List<ResolvedHolidayDay>();
        foreach (var (year, entries) in years.OrderBy(x => x.Key))
        {
            foreach (var group in entries.GroupBy(x => x.Name, StringComparer.Ordinal))
            {
                var makeups = group.Where(x => !x.IsOffDay).OrderBy(x => x.Date).ToList();
                var offWeekdays = group.Where(x => x.IsOffDay && IsoWeekday(x.Date) <= 5).OrderBy(x => x.Date).ToList();
                var paired = offWeekdays.Skip(Math.Max(0, offWeekdays.Count - makeups.Count)).ToList();
                for (var i = 0; i < makeups.Count; i++)
                {
                    var date = makeups[i].Date;
                    if (!seen.Add(date)) continue;
                    int? auto = i < paired.Count ? IsoWeekday(paired[i].Date) : null;
                    var (follow, source) = manual.TryGetValue(date, out var chosen)
                        ? (chosen.FollowWeekday, chosen.FollowWeekday is null ? HolidayFollowSources.Skip : HolidayFollowSources.Manual)
                        : (auto, auto is null ? HolidayFollowSources.Unresolved : HolidayFollowSources.Auto);
                    result.Add(new ResolvedHolidayDay(year, date, HolidayDayKinds.Makeup, group.Key, auto, follow, source));
                }
                foreach (var off in group.Where(x => x.IsOffDay))
                    if (seen.Add(off.Date))
                        result.Add(new ResolvedHolidayDay(year, off.Date, HolidayDayKinds.Off, group.Key, null, null, null));
            }
        }
        return result.OrderBy(x => x.Date).ToList();
    }

    public static HolidayCalendar ToCalendar(
        IReadOnlyList<ResolvedHolidayDay> days, bool enabled, DateOnly today, DateTimeOffset generatedAt) => new()
    {
        Enabled = enabled,
        GeneratedAt = generatedAt,
        Days = !enabled
            ? []
            : days.Where(x => x.Date >= today.AddDays(-WindowPastDays) && x.Date <= today.AddDays(WindowFutureDays))
                .Select(x => new HolidayCalendarDay
                {
                    Date = Format(x.Date),
                    Kind = x.Kind,
                    Name = x.Name,
                    FollowWeekday = x.FollowWeekday,
                    FollowSource = x.FollowSource,
                })
                .ToList(),
    };

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
