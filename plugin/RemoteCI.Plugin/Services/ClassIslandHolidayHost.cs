using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// “启用课表”开关是宿主 LessonsService 的公开运行时属性（不在 ILessonsService 接口上、也不落盘），
/// 因此按名称反射访问；宿主改名或移除时返回 null，放假日处理自动降级。
/// </summary>
public static class ClassPlanSwitch
{
    public static PropertyInfo? Resolve(object lessons) =>
        lessons.GetType().GetProperty("IsClassPlanEnabled", BindingFlags.Instance | BindingFlags.Public) is
            { CanRead: true, CanWrite: true } property && property.PropertyType == typeof(bool)
            ? property
            : null;
}

public sealed class ClassIslandHolidayHost(ILessonsService lessons, IProfileService profiles) : IHolidayHostOperations
{
    private readonly PropertyInfo? _switch = ClassPlanSwitch.Resolve(lessons);

    public bool? IsClassPlanEnabled
    {
        get => _switch?.GetValue(lessons) as bool?;
        set
        {
            if (value is { } enabled) _switch?.SetValue(lessons, enabled);
        }
    }

    public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => lessons.GetClassPlanByDate(date.Date, out planId);

    public Guid? GetOrderedSchedulePlanId(DateTime date) =>
        OrderedSchedules.TryGetValue(date.Date, out var schedule) ? schedule.ClassPlanId : null;

    public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date) =>
        profiles.CreateTempClassPlan(sourcePlanId, enableDateTime: date.Date);

    public void RemoveOrderedSchedule(DateTime date, Guid planId)
    {
        var ordered = OrderedSchedules;
        if (!ordered.TryGetValue(date.Date, out var schedule) || schedule.ClassPlanId != planId) return;
        ordered.Remove(date.Date);
        var plans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(profiles.Profile, "ClassPlans");
        // 只删临时层，绝不删除管理员的源课表。
        if (plans.TryGetValue(planId, out var plan) && plan.IsOverlay) plans.Remove(planId);
    }

    public void SaveProfile() => profiles.SaveProfile();

    private IDictionary<DateTime, OrderedSchedule> OrderedSchedules =>
        HostApiCompat.ReadProperty<IDictionary<DateTime, OrderedSchedule>>(profiles.Profile, "OrderedSchedules");
}
