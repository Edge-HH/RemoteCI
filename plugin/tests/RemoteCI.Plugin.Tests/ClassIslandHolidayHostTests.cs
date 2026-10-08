using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ClassIslandHolidayHostTests
{
    private sealed class LessonsWithSwitch { public bool IsClassPlanEnabled { get; set; } = true; }
    private sealed class LessonsWithoutSwitch { }

    [Fact]
    public void ClassPlanSwitch_ResolvesWritableBoolProperty()
    {
        var lessons = new LessonsWithSwitch();
        var property = ClassPlanSwitch.Resolve(lessons);

        Assert.NotNull(property);
        property!.SetValue(lessons, false);
        Assert.False(lessons.IsClassPlanEnabled);
        Assert.Null(ClassPlanSwitch.Resolve(new LessonsWithoutSwitch()));
    }

    [Fact]
    public void Profile_ExposesWritableOrderedSchedulesAndClassPlans()
    {
        var profile = new Profile();

        var ordered = HostApiCompat.ReadProperty<IDictionary<DateTime, OrderedSchedule>>(profile, "OrderedSchedules");
        var plans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(profile, "ClassPlans");
        var id = Guid.NewGuid();
        ordered[new DateTime(2026, 10, 10)] = new OrderedSchedule { ClassPlanId = id };
        plans[id] = new ClassPlan { IsOverlay = true };

        Assert.True(ordered.Remove(new DateTime(2026, 10, 10)));
        Assert.True(plans.Remove(id));
    }
}
