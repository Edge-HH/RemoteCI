namespace RemoteCI.Shared.Models;

/// <summary>表格中的一节课：课程本身与它所在的班级（日程中用于显示班级、跳转到该班课表）。</summary>
public sealed record ScheduleGridEntry(CourseEntry Course, Guid? ClassId = null, string? ClassName = null)
{
    /// <summary>自定义课程按时间放进表格，班级课程按节次放。</summary>
    public bool IsPersonal => Course.PersonalCourseId is not null;
}

/// <summary>表格的一列（一天）；<paramref name="HasData"/> 为 false 表示超出可浏览范围，显示“暂无课表数据”。</summary>
public sealed record ScheduleGridDay(
    DateOnly Date, IReadOnlyList<ScheduleGridEntry> Entries, string? DayKind = null, string? HolidayName = null, bool HasData = true);

/// <summary>
/// 表格的一行：节次行（<see cref="Extra"/> 为 false）或不与任何节次重叠的自定义课程按开始时间追加的行。
/// <see cref="GapBefore"/> 时与上一行之间留空白，<see cref="DividerBefore"/> 时画一条细线。
/// </summary>
public sealed record ScheduleGridRow(
    string Key, string Label, string? StartTime, string? EndTime, bool GapBefore, bool DividerBefore, bool Extra);

/// <summary>
/// 一个色块：从第 <see cref="RowStart"/> 行起占 <see cref="RowSpan"/> 行；同格有多个色块时左右平分，
/// 本块位于第 <see cref="Lane"/> 道、共 <see cref="LaneCount"/> 道。<see cref="Indexes"/> 是合并后包含的节次。
/// </summary>
public sealed class ScheduleGridBlock
{
    public required ScheduleGridEntry Entry { get; init; }
    public int RowStart { get; set; }
    public int RowSpan { get; set; } = 1;
    public int Lane { get; set; }
    public int LaneCount { get; set; } = 1;
    public List<int> Indexes { get; } = [];
}

public sealed record ScheduleGridColumn(
    DateOnly Date, bool IsToday, string? DayKind, string? HolidayName, bool HasData, IReadOnlyList<ScheduleGridBlock> Blocks)
{
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

/// <summary><see cref="HasWeekendEntries"/> 供“显示周末：自动”判断本周周末是否有课。</summary>
public sealed record ScheduleGridResult(
    IReadOnlyList<ScheduleGridRow> Rows, IReadOnlyList<ScheduleGridColumn> Columns, bool HasWeekendEntries);

/// <summary>
/// 多彩课程表的排布：行是节次，列是日期。班级课程按节次放；自定义课程占据与它时间重叠的所有节次行，
/// 不与任何节次重叠的按开始时间单独成行；相邻节次学科、老师、地点、班级都相同且中间没有空白或分割线时合并为连堂。
/// 网页直接使用本类，Android 端 ScheduleGridLogic.kt 是它的移植，两端共用 shared/test-fixtures/schedule-grid 校验。
/// </summary>
public static class ScheduleGrid
{
    public static ScheduleGridResult Build(
        IReadOnlyList<SchedulePeriod> periods, IReadOnlyList<ScheduleGridDay> days, DateOnly today, bool mergeConsecutive)
    {
        var rows = periods
            .Select(p => new ScheduleGridRow($"p{p.Index}", (p.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                p.StartTime, p.EndTime, false, p.DividerBefore, false))
            .ToList();
        var periodRanges = periods
            .Select(p => (Start: ScheduleTimeAxis.Minutes(p.StartTime), End: ScheduleTimeAxis.Minutes(p.EndTime)))
            .ToList();

        // 不与任何节次重叠的自定义课程：按开始时间成行，插到时间上相应的位置。
        var extras = days.SelectMany(day => day.Entries)
            .Where(entry => entry.IsPersonal && Overlaps(entry, periodRanges).Count == 0 && ScheduleTimeAxis.Minutes(entry.Course.StartTime) is not null)
            .GroupBy(entry => ScheduleTimeAxis.Minutes(entry.Course.StartTime)!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new ScheduleGridRow($"x{Clock(group.Key)}", string.Empty, Clock(group.Key),
                Clock(group.Max(entry => ScheduleTimeAxis.Minutes(entry.Course.EndTime) ?? group.Key)), false, false, true))
            .ToList();
        foreach (var extra in extras)
        {
            var start = ScheduleTimeAxis.Minutes(extra.StartTime);
            var position = 0;
            for (var i = 0; i < rows.Count; i++)
                if (ScheduleTimeAxis.Minutes(rows[i].StartTime) is { } rowStart && rowStart <= start) position = i + 1;
            rows.Insert(position, extra);
        }
        for (var i = 1; i < rows.Count; i++)
            rows[i] = rows[i] with { GapBefore = ScheduleTimeAxis.IsGap(rows[i - 1].EndTime, rows[i].StartTime) };

        var rowOfPeriod = new Dictionary<int, int>();
        var rowOfExtra = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Extra) rowOfPeriod[periods.First(p => $"p{p.Index}" == rows[i].Key).Index] = i;
            else rowOfExtra[ScheduleTimeAxis.Minutes(rows[i].StartTime)!.Value] = i;
        }
        var rowRanges = rows.Select(r => (Start: ScheduleTimeAxis.Minutes(r.StartTime), End: ScheduleTimeAxis.Minutes(r.EndTime), r.Extra)).ToList();

        var columns = new List<ScheduleGridColumn>();
        foreach (var day in days)
        {
            var blocks = new List<ScheduleGridBlock>();
            foreach (var entry in day.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Course.Subject)))
            {
                if (entry.IsPersonal)
                {
                    var covered = Enumerable.Range(0, rows.Count)
                        .Where(i => !rowRanges[i].Extra && Overlap(entry, rowRanges[i].Start, rowRanges[i].End))
                        .ToList();
                    if (covered.Count > 0)
                        blocks.Add(new ScheduleGridBlock { Entry = entry, RowStart = covered[0], RowSpan = covered[^1] - covered[0] + 1 });
                    else if (ScheduleTimeAxis.Minutes(entry.Course.StartTime) is { } start && rowOfExtra.TryGetValue(start, out var row))
                        blocks.Add(new ScheduleGridBlock { Entry = entry, RowStart = row });
                }
                else if (rowOfPeriod.TryGetValue(entry.Course.Index, out var row))
                {
                    var block = new ScheduleGridBlock { Entry = entry, RowStart = row };
                    block.Indexes.Add(entry.Course.Index);
                    blocks.Add(block);
                }
            }
            if (mergeConsecutive) blocks = Merge(blocks, rows);
            AssignLanes(blocks);
            columns.Add(new ScheduleGridColumn(day.Date, day.Date == today, day.DayKind, day.HolidayName, day.HasData, blocks));
        }
        var weekend = days.Any(day => day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday &&
                                      day.Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Course.Subject)));
        return new ScheduleGridResult(rows, columns, weekend);
    }

    /// <summary>连堂合并的依据：学科、老师、地点、班级、启用状态与调课标记都相同。</summary>
    public static string MergeKey(ScheduleGridEntry entry) => string.Join('\u001f',
        SubjectColors.Key(entry.Course.Subject), entry.Course.Teacher?.Trim(), entry.Course.Location?.Trim(), entry.ClassId,
        entry.Course.Enabled, entry.Course.AdjustmentId is not null, entry.Course.AdjustmentPending == true);

    private static List<ScheduleGridBlock> Merge(List<ScheduleGridBlock> blocks, IReadOnlyList<ScheduleGridRow> rows)
    {
        var occupancy = new int[rows.Count];
        foreach (var block in blocks)
            for (var i = block.RowStart; i < block.RowStart + block.RowSpan; i++) occupancy[i]++;
        var result = new List<ScheduleGridBlock>();
        ScheduleGridBlock? previous = null;
        foreach (var block in blocks.OrderBy(x => x.RowStart).ThenBy(x => x.Entry.IsPersonal))
        {
            if (previous is not null && !block.Entry.IsPersonal && !previous.Entry.IsPersonal &&
                block.RowStart == previous.RowStart + previous.RowSpan && occupancy[block.RowStart] == 1 &&
                occupancy[previous.RowStart + previous.RowSpan - 1] == 1 &&
                !rows[block.RowStart].GapBefore && !rows[block.RowStart].DividerBefore &&
                MergeKey(block.Entry) == MergeKey(previous.Entry))
            {
                previous.RowSpan += block.RowSpan;
                previous.Indexes.AddRange(block.Indexes);
                continue;
            }
            result.Add(block);
            previous = block.Entry.IsPersonal || occupancy[block.RowStart] != 1 ? null : block;
        }
        return result;
    }

    /// <summary>
    /// 同一列中时间重叠的色块按贪心分道，相互重叠的一组色块平分该格宽度。
    /// 排序须稳定（先开始行、再跨行多的、再班级课程在前，其余保持原顺序），Kotlin 移植依赖同一顺序。
    /// </summary>
    private static void AssignLanes(List<ScheduleGridBlock> blocks)
    {
        var sorted = blocks.OrderBy(x => x.RowStart).ThenByDescending(x => x.RowSpan).ThenBy(x => x.Entry.IsPersonal).ToList();
        blocks.Clear();
        blocks.AddRange(sorted);
        var cluster = new List<ScheduleGridBlock>();
        var laneEnds = new List<int>();
        var clusterEnd = -1;
        foreach (var block in blocks)
        {
            if (block.RowStart >= clusterEnd && cluster.Count > 0)
            {
                foreach (var member in cluster) member.LaneCount = laneEnds.Count;
                cluster.Clear();
                laneEnds.Clear();
            }
            var lane = laneEnds.FindIndex(end => end <= block.RowStart);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(0);
            }
            laneEnds[lane] = block.RowStart + block.RowSpan;
            block.Lane = lane;
            cluster.Add(block);
            clusterEnd = Math.Max(cluster.Count == 1 ? 0 : clusterEnd, block.RowStart + block.RowSpan);
        }
        foreach (var member in cluster) member.LaneCount = laneEnds.Count;
    }

    private static List<int> Overlaps(ScheduleGridEntry entry, IReadOnlyList<(int? Start, int? End)> ranges) =>
        Enumerable.Range(0, ranges.Count).Where(i => Overlap(entry, ranges[i].Start, ranges[i].End)).ToList();

    private static bool Overlap(ScheduleGridEntry entry, int? rowStart, int? rowEnd) =>
        ScheduleTimeAxis.Minutes(entry.Course.StartTime) is { } start && ScheduleTimeAxis.Minutes(entry.Course.EndTime) is { } end &&
        rowStart is { } from && rowEnd is { } to && start < to && end > from;

    private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}
