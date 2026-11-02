using System.Globalization;
using System.Text.Json.Nodes;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>周课表排布、节次轴与学科颜色；夹具与 Android 端测试共用（shared/test-fixtures）。</summary>
public sealed class ScheduleGridTests
{
    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "test-fixtures", .. parts]);

    public static IEnumerable<object[]> GridFixtures() =>
        Directory.GetFiles(FixturePath("schedule-grid"), "*.json").Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(GridFixtures))]
    public void Build_MatchesSharedFixture(string file)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(FixturePath("schedule-grid", file)))!.AsObject();
        var periods = fixture["periods"]!.AsArray().Select(node => new SchedulePeriod
        {
            Index = (int)node!["index"]!,
            StartTime = (string?)node["startTime"],
            EndTime = (string?)node["endTime"],
            DividerBefore = (bool?)node["dividerBefore"] ?? false,
        }).ToList();
        var days = fixture["days"]!.AsArray().Select(node => new ScheduleGridDay(
            DateOnly.Parse((string)node!["date"]!, CultureInfo.InvariantCulture),
            node["entries"]!.AsArray().Select(entry => Entry(entry!.AsObject())).ToList(),
            (string?)node["dayKind"], (string?)node["holidayName"], (bool?)node["hasData"] ?? true)).ToList();

        var grid = ScheduleGrid.Build(periods, days, DateOnly.Parse((string)fixture["today"]!, CultureInfo.InvariantCulture), (bool)fixture["merge"]!);

        var expected = fixture["expected"]!;
        Assert.Equal(Strings(expected["rows"]), grid.Rows.Select(row =>
            $"{row.Key}|{row.Label}|{row.StartTime}|{row.EndTime}|{(row.GapBefore ? "gap" : "-")}|{(row.DividerBefore ? "div" : "-")}"));
        Assert.Equal(Strings(expected["columns"]), grid.Columns.Select(column =>
            $"{column.Date:yyyy-MM-dd}|{(column.IsToday ? 1 : 0)}|{column.DayKind ?? "-"}|{(column.HasData ? 1 : 0)}"));
        Assert.Equal(Strings(expected["blocks"]), grid.Columns.SelectMany(column => column.Blocks.Select(block =>
            $"{column.Date:yyyy-MM-dd}|{block.RowStart}+{block.RowSpan}|{block.Lane}/{block.LaneCount}|{block.Entry.Course.Subject}|{string.Join(',', block.Indexes)}")));
        Assert.Equal((bool)expected["hasWeekend"]!, grid.HasWeekendEntries);
    }

    [Fact]
    public void SubjectColors_MatchSharedFixture()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(FixturePath("subject-colors.json")))!.AsObject();
        Assert.Equal(Strings(fixture["palette"]), SubjectColors.Palette);
        foreach (var subject in fixture["subjects"]!.AsArray())
        {
            var name = (string)subject!["name"]!;
            Assert.Equal((int)subject["swatch"]!, SubjectColors.DefaultSwatch(name));
            Assert.Equal((string)subject["color"]!, SubjectColors.Default(name));
        }
        foreach (var derived in fixture["derived"]!.AsArray())
        {
            var color = (string)derived!["color"]!;
            Assert.Equal(((string)derived["light"]!["background"]!, (string)derived["light"]!["foreground"]!), SubjectColors.Derive(color, dark: false));
            Assert.Equal(((string)derived["dark"]!["background"]!, (string)derived["dark"]!["foreground"]!), SubjectColors.Derive(color, dark: true));
        }
    }

    [Fact]
    public void SubjectColors_CommonSubjectsAreDistinctAndPaletteChecked()
    {
        string[] common = ["语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理", "信息技术", "通用技术", "体育", "音乐", "美术", "心理", "班会", "自习"];
        Assert.Equal(common.Length, common.Select(SubjectColors.Default).Distinct().Count());
        Assert.Equal(SubjectColors.Default("政治"), SubjectColors.Default("道德与法治"));
        Assert.True(SubjectColors.IsPaletteColor("#6f9fe0"));
        Assert.Equal("#6F9FE0", SubjectColors.NormalizePaletteColor(" #6f9fe0 "));
        Assert.False(SubjectColors.IsPaletteColor("#123456"));
    }

    [Fact]
    public void TimeAxis_UsesMostCommonLayoutAndAppendsExtraPeriods()
    {
        static CourseEntry Lesson(int index, string start, string end) => new() { Index = index, Subject = "课", StartTime = start, EndTime = end };
        List<CourseEntry> regular =
        [
            Lesson(0, "08:00", "08:45"), Lesson(1, "08:55", "09:40"), Lesson(2, "14:00", "14:45"),
        ];
        List<CourseEntry> shortened = [Lesson(0, "08:00", "08:40"), Lesson(1, "08:50", "09:30")];
        List<CourseEntry> evening = [.. regular, Lesson(3, "19:00", "19:45")];

        var periods = ScheduleTimeAxis.Build(
        [
            new(shortened),
            new(regular, [0]),
            new(regular, [0]),
            new(evening),
        ]);

        Assert.Equal(["0:08:00-08:45", "1:08:55-09:40", "2:14:00-14:45", "3:19:00-19:45"],
            periods.Select(p => $"{p.Index}:{p.StartTime}-{p.EndTime}"));
        Assert.True(periods[1].DividerBefore);
        Assert.False(periods[2].DividerBefore);
        Assert.False(periods[1].GapBefore);
        Assert.True(periods[2].GapBefore);
        Assert.True(periods[3].GapBefore);
        Assert.Empty(ScheduleTimeAxis.Build([]));
    }

    [Fact]
    public void DividerAfterIndexes_IgnoresLeadingAndTrailingDividers()
    {
        // 分割线 上课 课间 上课 分割线 上课 分割线
        Assert.Equal([1], ProfileDocument.DividerAfterIndexes([2, 0, 1, 0, 2, 0, 2]));
        Assert.Null(ProfileDocument.DividerAfterIndexes([0, 1, 0]));
    }

    private static ScheduleGridEntry Entry(JsonObject node)
    {
        var personal = (bool?)node["personal"] ?? false;
        var course = new CourseEntry
        {
            Index = (int?)node["index"] ?? 0,
            Subject = (string)node["subject"]!,
            Teacher = (string?)node["teacher"],
            Location = (string?)node["location"],
            StartTime = (string?)node["startTime"],
            EndTime = (string?)node["endTime"],
            Enabled = (bool?)node["enabled"] ?? true,
            PersonalCourseId = personal ? Guid.NewGuid() : null,
            AdjustmentId = (bool?)node["adjusted"] == true ? Guid.NewGuid() : null,
            AdjustmentPending = (bool?)node["pending"] == true ? true : null,
        };
        var classId = (string?)node["classId"];
        return new ScheduleGridEntry(course, classId is null ? null : Guid.Parse(classId), (string?)node["className"]);
    }

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(x => (string)x!).ToList();
}
