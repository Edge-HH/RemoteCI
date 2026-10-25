using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class HolidayScheduleApplierTests
{
    private sealed class FakeHost : IHolidayHostOperations
    {
        public bool? IsClassPlanEnabled { get; set; } = true;
    }

    private static HolidayScheduleApplier Create(FakeHost host) => new(host, NullLogger<HolidayScheduleApplier>.Instance);

    private static readonly DateTime Oct1 = new(2026, 10, 1, 7, 30, 0);
    private static readonly DateTime Oct8 = new(2026, 10, 8, 7, 30, 0);

    [Fact]
    public void OffDay_DisablesClassPlanAndRestoresNextSchoolDay()
    {
        var host = new FakeHost();
        var applier = Create(host);

        Assert.True(applier.Apply(HolidayCalendarStoreTests.National(), Oct1));
        Assert.False(host.IsClassPlanEnabled);

        Assert.True(applier.Apply(HolidayCalendarStoreTests.National(), Oct8));
        Assert.True(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_DoesNotRestoreSwitchTurnedOffByUser()
    {
        var host = new FakeHost { IsClassPlanEnabled = false };
        var applier = Create(host);

        applier.Apply(HolidayCalendarStoreTests.National(), Oct1);
        applier.Apply(HolidayCalendarStoreTests.National(), Oct8);

        Assert.False(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_ManualReenableSameDayIsRespected()
    {
        var host = new FakeHost();
        var applier = Create(host);
        applier.Apply(HolidayCalendarStoreTests.National(), Oct1);

        host.IsClassPlanEnabled = true;
        applier.Apply(HolidayCalendarStoreTests.National(), Oct1.AddHours(2));

        Assert.True(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_UnsupportedHostSkipsSwitch()
    {
        var host = new FakeHost { IsClassPlanEnabled = null };

        Assert.False(Create(host).Apply(HolidayCalendarStoreTests.National(), Oct1));

        Assert.Null(host.IsClassPlanEnabled);
    }

    [Fact]
    public void DisabledCalendar_RestoresSwitch()
    {
        var host = new FakeHost();
        var applier = Create(host);
        applier.Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7));
        Assert.False(host.IsClassPlanEnabled);

        applier.Apply(new HolidayCalendar { Enabled = false }, new DateTime(2026, 10, 7, 9, 0, 0));

        Assert.True(host.IsClassPlanEnabled);
    }

    [Theory]
    [InlineData("2026-10-10", 3, "2026-10-07")]
    [InlineData("2026-09-20", 2, "2026-09-15")]
    [InlineData("2026-02-28", 1, "2026-02-23")]
    public void SourceDate_UsesSameMondayBasedWeek(string makeup, int weekday, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), HolidayScheduleApplier.SourceDate(DateTime.Parse(makeup), weekday));
    }
}
