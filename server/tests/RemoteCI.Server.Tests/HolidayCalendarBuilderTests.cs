using System.Text.Json;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayCalendarBuilderTests
{
    [Fact]
    public void Calendar_SerializesWithProtocolFieldNames()
    {
        var envelope = Envelope.HolidayCalendar(new HolidayCalendar
        {
            Enabled = true,
            Days =
            [
                new HolidayCalendarDay { Date = "2026-10-10", Kind = HolidayDayKinds.Makeup, Name = "国庆节", FollowWeekday = 3, FollowSource = HolidayFollowSources.Auto },
                new HolidayCalendarDay { Date = "2026-10-01", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            ],
        });

        var json = JsonSerializer.Serialize(envelope, JsonDefaults.Options);

        Assert.Contains("\"type\":\"holiday_calendar\"", json);
        Assert.Contains("\"followWeekday\":3", json);
        Assert.Contains("\"followSource\":\"auto\"", json);
        Assert.DoesNotContain("\"followWeekday\":null", json);
        Assert.Contains(RemoteCiCapabilities.HolidayCalendar, RemoteCiCapabilities.Current);
        Assert.DoesNotContain(RemoteCiCapabilities.HolidayCalendar, RemoteCiCapabilities.Baseline);
        Assert.Equal("调休自动适配", RemoteCiCapabilities.ChineseName(RemoteCiCapabilities.HolidayCalendar));
    }

    [Fact]
    public void ScheduleDay_OmitsHolidayFieldsOnNormalDays()
    {
        var normal = JsonSerializer.Serialize(new ScheduleDay { Date = "2026-10-09" }, JsonDefaults.Options);
        var holiday = JsonSerializer.Serialize(
            new ScheduleDay { Date = "2026-10-01", DayKind = ScheduleDayKinds.Holiday, HolidayName = "国庆节" }, JsonDefaults.Options);

        Assert.DoesNotContain("dayKind", normal);
        Assert.Contains("\"dayKind\":\"holiday\"", holiday);
        Assert.Contains("\"holidayName\":", holiday);
    }

    [Theory]
    [InlineData("2026-01-04", 5)]
    [InlineData("2026-02-14", 5)]
    [InlineData("2026-02-28", 1)]
    [InlineData("2026-05-09", 2)]
    [InlineData("2026-09-20", 2)]
    [InlineData("2026-10-10", 3)]
    public void Resolve_PairsMakeupDaysWithLastOffWeekdays(string date, int weekday)
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(), []);

        var day = Assert.Single(days, x => x.Date == DateOnly.Parse(date));
        Assert.Equal(HolidayDayKinds.Makeup, day.Kind);
        Assert.Equal(weekday, day.AutoWeekday);
        Assert.Equal(weekday, day.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Auto, day.FollowSource);
    }

    [Fact]
    public void Resolve_OverridesWinAndNullMeansSkip()
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(),
        [
            new HolidayOverride(new DateOnly(2026, 10, 10), 5),
            new HolidayOverride(new DateOnly(2026, 9, 20), null),
        ]);

        var oct10 = Assert.Single(days, x => x.Date == new DateOnly(2026, 10, 10));
        Assert.Equal((3, 5, HolidayFollowSources.Manual), (oct10.AutoWeekday!.Value, oct10.FollowWeekday!.Value, oct10.FollowSource));
        var sep20 = Assert.Single(days, x => x.Date == new DateOnly(2026, 9, 20));
        Assert.Null(sep20.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Skip, sep20.FollowSource);
    }

    [Fact]
    public void Resolve_MarksMakeupUnresolvedWhenNoOffWeekdayToPair()
    {
        var entries = new Dictionary<int, IReadOnlyList<HolidayEntry>>
        {
            [2026] = [new(new DateOnly(2026, 3, 7), "测试假", false), new(new DateOnly(2026, 3, 8), "测试假", true)],
        };

        var makeup = Assert.Single(HolidayCalendarBuilder.Resolve(entries, []), x => x.Kind == HolidayDayKinds.Makeup);

        Assert.Null(makeup.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Unresolved, makeup.FollowSource);
    }

    [Fact]
    public void ToCalendar_KeepsOnlyWindowAndEmptiesWhenDisabled()
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(), []);
        var today = new DateOnly(2026, 10, 2);

        var calendar = HolidayCalendarBuilder.ToCalendar(days, enabled: true, today, DateTimeOffset.UnixEpoch);
        var disabled = HolidayCalendarBuilder.ToCalendar(days, enabled: false, today, DateTimeOffset.UnixEpoch);

        Assert.Equal("2026-10-01", calendar.Days.Min(x => x.Date));
        Assert.Contains(calendar.Days, x => x is { Date: "2026-10-10", Kind: HolidayDayKinds.Makeup, FollowWeekday: 3 });
        Assert.DoesNotContain(calendar.Days, x => x.Date == "2026-09-20");
        Assert.False(disabled.Enabled);
        Assert.Empty(disabled.Days);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2026-13-01","isOffDay":true}]}""")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2031-10-01","isOffDay":true}]}""")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2026-10-01"}]}""")]
    [InlineData("""{"days":[{"name":"","date":"2026-10-01","isOffDay":true}]}""")]
    [InlineData("""{"year":2026}""")]
    [InlineData("[]")]
    public void Parse_RejectsMalformedPayloads(string json)
    {
        Assert.Throws<InvalidDataException>(() => HolidayDataParser.Parse(2026, json));
    }

    [Fact]
    public void Parse_RejectsOversizedPayload()
    {
        var json = "{\"days\":[],\"pad\":\"" + new string('x', HolidayDataParser.MaxBytes) + "\"}";
        Assert.Throws<InvalidDataException>(() => HolidayDataParser.Parse(2026, json));
    }
}
