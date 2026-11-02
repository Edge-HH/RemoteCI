using System.Globalization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 按周浏览课表时的节次轴：一周里各天（日程中还有各班）的时间表可能不同，表格左侧的节次时间、节次之间的空白与分割线
/// 都取本周出现最多的那套时间表，其他天多出的节次按该节最常见的时间补在后面。
/// </summary>
public static class ScheduleTimeAxis
{
    /// <summary>相邻两节至少空出这么多分钟时，表格在两节之间留出空白（不画线、不写字）。</summary>
    public const int GapMinutes = 30;

    /// <summary>某天（日程中为某天某班）的一套时间表：课程的节次与起止时间，以及分割线位置。</summary>
    public sealed record Sample(IReadOnlyList<CourseEntry> Courses, IReadOnlyList<int>? DividerAfterIndexes = null);

    /// <summary>按出现次数选出本周的时间表；次数相同时取节次多的，再相同取先出现的（调用方按日期排序）。</summary>
    public static List<SchedulePeriod> Build(IEnumerable<Sample> samples)
    {
        var shapes = samples
            .Where(sample => sample.Courses.Count > 0)
            .Select(sample => new Shape(
                sample.Courses.OrderBy(x => x.Index).Select(x => (x.Index, x.StartTime, x.EndTime)).ToList(),
                (sample.DividerAfterIndexes ?? []).Distinct().OrderBy(x => x).ToList()))
            .ToList();
        if (shapes.Count == 0) return [];
        var best = shapes
            .Select((shape, order) => (shape, order))
            .GroupBy(x => x.shape.Signature)
            .Select(group => (group.First().shape, Count: group.Count(), Order: group.Min(x => x.order)))
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.shape.Lessons.Count)
            .ThenBy(x => x.Order)
            .First().shape;

        var periods = best.Lessons.Select(x => new SchedulePeriod { Index = x.Index, StartTime = x.Start, EndTime = x.End }).ToList();
        var known = periods.Select(x => x.Index).ToHashSet();
        foreach (var index in shapes.SelectMany(x => x.Lessons).Select(x => x.Index).Where(x => !known.Contains(x)).Distinct().OrderBy(x => x).ToList())
        {
            var common = shapes.SelectMany(x => x.Lessons).Where(x => x.Index == index)
                .Select((lesson, order) => (lesson, order))
                .GroupBy(x => (x.lesson.Start, x.lesson.End))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Min(x => x.order))
                .First().Key;
            periods.Add(new SchedulePeriod { Index = index, StartTime = common.Start, EndTime = common.End });
        }
        periods.Sort((left, right) => left.Index.CompareTo(right.Index));
        for (var i = 1; i < periods.Count; i++)
        {
            periods[i].GapBefore = IsGap(periods[i - 1].EndTime, periods[i].StartTime);
            periods[i].DividerBefore = best.Dividers.Contains(periods[i - 1].Index);
        }
        return periods;
    }

    /// <summary>上一段结束与下一段开始之间是否空出至少 <see cref="GapMinutes"/> 分钟；任一时间未知时不算。</summary>
    public static bool IsGap(string? previousEnd, string? nextStart) =>
        Minutes(previousEnd) is { } end && Minutes(nextStart) is { } start && start - end >= GapMinutes;

    /// <summary>"HH:mm"（或 "HH:mm:ss"）换算为当天的分钟数；无法解析返回 null。</summary>
    public static int? Minutes(string? time) =>
        TimeSpan.TryParseExact(time?.Trim(), ["hh\\:mm", "hh\\:mm\\:ss", "h\\:mm"], CultureInfo.InvariantCulture, out var value)
            ? (int)value.TotalMinutes
            : null;

    private sealed record Shape(List<(int Index, string? Start, string? End)> Lessons, List<int> Dividers)
    {
        public string Signature { get; } =
            string.Join(';', Lessons.Select(x => $"{x.Index}:{x.Start}-{x.End}")) + "|" + string.Join(',', Dividers);
    }
}
