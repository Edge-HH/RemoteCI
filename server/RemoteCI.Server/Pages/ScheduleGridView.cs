using System.Globalization;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 周课表表格（Shared/_ScheduleGrid.cshtml）的视图模型：课表页、我的日程与访客页共用。
/// “自动合并连堂”等显示设置保存在浏览器本地，因此合并与不合并两种排法都渲染，由根元素属性选择显示哪一种。
/// </summary>
public sealed class ScheduleGridView
{
    private static readonly string[] WeekNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private readonly Dictionary<string, string> _styles = new(StringComparer.Ordinal);

    public required ScheduleGridResult Merged { get; init; }
    public required ScheduleGridResult Unmerged { get; init; }
    /// <summary>用户改过的学科颜色；访客页为空，全部用默认颜色。</summary>
    public IReadOnlyDictionary<string, string> Colors { get; init; } = new Dictionary<string, string>();
    /// <summary>我的日程：色块显示班级而不是老师，自定义课程可点开修改。</summary>
    public bool Mine { get; init; }

    public IReadOnlyList<ScheduleGridRow> Rows => Merged.Rows;
    public IReadOnlyList<ScheduleGridColumn> Columns => Merged.Columns;
    public string MonthLabel => Columns.Count == 0 ? string.Empty : $"{Columns[0].Date.Month}月";

    /// <summary>每行在 CSS 网格中的轨道号（从 1 开始）；空白与分割线各占一条窄轨道。</summary>
    public IReadOnlyList<int> Tracks { get; private init; } = [];
    public IReadOnlyList<int> DividerTracks { get; private init; } = [];
    public string TrackTemplate { get; private init; } = string.Empty;

    public static ScheduleGridView ForClass(ScheduleWeekResponse week, IReadOnlyDictionary<string, string> colors) =>
        Create(week.Periods, week.From, week.MinDate, week.MaxDate, week.Today, colors, mine: false, date =>
        {
            var day = week.Days.FirstOrDefault(x => x.Date == date);
            return day is null
                ? (null, null, [])
                : (day.DayKind, day.HolidayName, day.Enabled ? day.Courses.Where(x => x.Enabled).Select(x => new ScheduleGridEntry(x)).ToList() : []);
        });

    public static ScheduleGridView ForMine(MyScheduleWeekResponse week, IReadOnlyDictionary<string, string> colors) =>
        Create(week.Periods, week.From, week.MinDate, week.MaxDate, week.Today, colors, mine: true, date =>
        {
            var day = week.Days.FirstOrDefault(x => x.Date == date);
            return day is null
                ? (null, null, [])
                : (day.DayKind, day.HolidayName, day.Items.SelectMany(item => item.Courses.Where(x => x.Enabled).Select(course =>
                    new ScheduleGridEntry(course, item.ClassId == Guid.Empty ? null : item.ClassId, item.ClassName))).ToList());
        });

    private static ScheduleGridView Create(
        List<SchedulePeriod> periods, string from, string minDate, string maxDate, string today,
        IReadOnlyDictionary<string, string> colors, bool mine,
        Func<string, (string? Kind, string? Holiday, List<ScheduleGridEntry> Entries)> entries)
    {
        var start = SemesterWeeks.Parse(from) ?? DateOnly.FromDateTime(DateTime.Today);
        var min = SemesterWeeks.Parse(minDate);
        var max = SemesterWeeks.Parse(maxDate);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = start.AddDays(offset);
            var (kind, holiday, list) = entries(SemesterWeeks.Format(date));
            return new ScheduleGridDay(date, list, kind, holiday, (min is null || date >= min) && (max is null || date <= max));
        }).ToList();
        var todayDate = SemesterWeeks.Parse(today) ?? DateOnly.FromDateTime(DateTime.Today);
        var merged = ScheduleGrid.Build(periods, days, todayDate, mergeConsecutive: true);
        var tracks = new List<int>();
        var dividers = new List<int>();
        var template = new List<string>();
        foreach (var row in merged.Rows)
        {
            if (row.GapBefore && template.Count > 0) template.Add("var(--sg-gap)");
            if (row.DividerBefore && template.Count > 0)
            {
                template.Add("var(--sg-divider)");
                dividers.Add(template.Count);
            }
            template.Add("minmax(var(--sg-row), auto)");
            tracks.Add(template.Count);
        }
        return new ScheduleGridView
        {
            Merged = merged,
            Unmerged = ScheduleGrid.Build(periods, days, todayDate, mergeConsecutive: false),
            Colors = colors,
            Mine = mine,
            Tracks = tracks,
            DividerTracks = dividers,
            TrackTemplate = string.Join(' ', template),
        };
    }

    public static string WeekName(DateOnly date) => WeekNames[(int)date.DayOfWeek];

    public static string ShortDate(DateOnly date) => date.ToString("MM/dd", CultureInfo.InvariantCulture);

    /// <summary>色块的 grid-row：从第几条轨道到第几条轨道（含中间的空白与分割线轨道）。</summary>
    public string GridRow(ScheduleGridBlock block) =>
        $"{Tracks[block.RowStart]} / {Tracks[block.RowStart + block.RowSpan - 1] + 1}";

    /// <summary>学科颜色的 CSS 变量：浅色与深色模式各一对底色与文字色。</summary>
    public string ColorStyle(string subject)
    {
        if (_styles.TryGetValue(subject, out var style)) return style;
        var color = Services.SubjectColorService.Resolve(Colors, subject);
        var light = SubjectColors.Derive(color, dark: false);
        var dark = SubjectColors.Derive(color, dark: true);
        return _styles[subject] = $"--sc-bg:{light.Background};--sc-fg:{light.Foreground};--sc-bg-d:{dark.Background};--sc-fg-d:{dark.Foreground}";
    }

    /// <summary>色块的完整 style：位置、分道与颜色。</summary>
    public string BlockStyle(ScheduleGridBlock block) =>
        $"grid-row:{GridRow(block)};--lane:{block.Lane};--lanes:{block.LaneCount};{ColorStyle(block.Entry.Course.Subject)}";
}
