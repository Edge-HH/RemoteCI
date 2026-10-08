using ClassIsland.Shared.Models.Profile;
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
        public Dictionary<DateTime, Guid> Ordered { get; } = [];
        public Dictionary<DayOfWeek, Guid> PlanIds { get; } = Enum.GetValues<DayOfWeek>()
            .Where(x => x is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            .ToDictionary(x => x, _ => Guid.NewGuid());
        public List<(Guid Source, DateTime Date)> Created { get; } = [];
        public List<DateTime> Removed { get; } = [];
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }

        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            planId = PlanIds.TryGetValue(date.DayOfWeek, out var id) ? id : null;
            return planId is null ? null : new ClassPlan { Name = date.DayOfWeek.ToString() };
        }

        public Guid? GetOrderedSchedulePlanId(DateTime date) => Ordered.TryGetValue(date.Date, out var id) ? id : null;

        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date)
        {
            var id = Guid.NewGuid();
            Created.Add((sourcePlanId, date.Date));
            Ordered[date.Date] = id;
            return id;
        }

        public void RemoveOrderedSchedule(DateTime date, Guid planId)
        {
            if (Ordered.TryGetValue(date.Date, out var id) && id == planId) Ordered.Remove(date.Date);
            Removed.Add(date.Date);
        }

        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }
    }

    private static string StatePath() =>
        Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "HolidayState.json");

    private static HolidayScheduleApplier Create(FakeHost host, string? statePath = null) =>
        new(host, new HolidayStateFile(statePath ?? StatePath()), NullLogger<HolidayScheduleApplier>.Instance);

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
    public void OffDay_UnsupportedHostSkipsSwitchButStillCreatesMakeup()
    {
        var host = new FakeHost { IsClassPlanEnabled = null };

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7, 8, 0, 0));

        Assert.Null(host.IsClassPlanEnabled);
        Assert.Single(host.Created);
    }

    [Theory]
    [InlineData("2026-10-10", 3, "2026-10-07")]
    [InlineData("2026-09-20", 2, "2026-09-15")]
    [InlineData("2026-02-28", 1, "2026-02-23")]
    public void SourceDate_UsesSameMondayBasedWeek(string makeup, int weekday, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), HolidayScheduleApplier.SourceDate(DateTime.Parse(makeup), weekday));
    }

    [Fact]
    public void Makeup_CreatesOnce_EvenAcrossInstances()
    {
        var host = new FakeHost();
        var statePath = StatePath();
        var today = new DateTime(2026, 10, 7, 8, 0, 0);

        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), today);
        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), today);

        var created = Assert.Single(host.Created);
        Assert.Equal((host.PlanIds[DayOfWeek.Wednesday], new DateTime(2026, 10, 10)), created);
        Assert.Equal(1, host.SaveCount);
    }

    [Fact]
    public void Makeup_LeavesForeignOrderedScheduleAlone()
    {
        var host = new FakeHost();
        var foreign = Guid.NewGuid();
        host.Ordered[new DateTime(2026, 10, 10)] = foreign;

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 8));

        Assert.Empty(host.Created);
        Assert.Equal(foreign, host.Ordered[new DateTime(2026, 10, 10)]);
    }

    [Fact]
    public void Makeup_OutsideLookaheadIsNotCreated()
    {
        var host = new FakeHost();

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 2));

        Assert.Empty(host.Created);
    }

    [Fact]
    public void Makeup_WeekdayChangeRebuilds()
    {
        var host = new FakeHost();
        var applier = Create(host);
        var today = new DateTime(2026, 10, 8);
        applier.Apply(HolidayCalendarStoreTests.National(followWeekday: 3), today);

        applier.Apply(HolidayCalendarStoreTests.National(followWeekday: 5), today);

        Assert.Equal([new DateTime(2026, 10, 10)], host.Removed);
        Assert.Equal(2, host.Created.Count);
        Assert.Equal(host.PlanIds[DayOfWeek.Friday], host.Created[^1].Source);
    }

    [Fact]
    public void Makeup_SkipRemovesOurPlan()
    {
        var host = new FakeHost();
        var applier = Create(host);
        var today = new DateTime(2026, 10, 8);
        applier.Apply(HolidayCalendarStoreTests.National(), today);

        applier.Apply(HolidayCalendarStoreTests.National(followWeekday: null), today);

        Assert.False(host.Ordered.ContainsKey(new DateTime(2026, 10, 10)));
    }

    [Fact]
    public void DisabledCalendar_RemovesFuturePlansAndRestoresSwitch()
    {
        var host = new FakeHost();
        var applier = Create(host);
        applier.Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7));
        Assert.False(host.IsClassPlanEnabled);
        Assert.Single(host.Ordered);

        applier.Apply(new HolidayCalendar { Enabled = false }, new DateTime(2026, 10, 7, 9, 0, 0));

        Assert.True(host.IsClassPlanEnabled);
        Assert.Empty(host.Ordered);
    }

    [Fact]
    public void Makeup_SaveFailureRollsBackAndRetriesLater()
    {
        var host = new FakeHost { ThrowOnSave = true };
        var applier = Create(host);
        var today = new DateTime(2026, 10, 8);

        applier.Apply(HolidayCalendarStoreTests.National(), today);
        Assert.Empty(host.Ordered);

        host.ThrowOnSave = false;
        applier.Apply(HolidayCalendarStoreTests.National(), today);
        Assert.Single(host.Ordered);
    }

    [Fact]
    public void PastRecords_AreForgottenWithoutTouchingProfile()
    {
        var host = new FakeHost();
        var statePath = StatePath();
        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 8));

        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 12));

        Assert.Empty(host.Removed);
        Assert.Empty(new HolidayStateFile(statePath).Makeups);
    }
}
