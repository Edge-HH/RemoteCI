using System.Text;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 托管临时层所需的宿主能力（ClassIsland 服务含 internal 成员，外部程序集无法实现假对象，故经此防腐层）。
/// 只能在 UI 线程调用。
/// </summary>
public interface IScheduleOverlayHost
{
    IReadOnlyDictionary<Guid, ClassPlan> ClassPlans { get; }
    bool SubjectExists(Guid subjectId);
    /// <summary>该日预定课表（OrderedSchedules[日期]）指向的课表 ID。</summary>
    Guid? GetOrderedPlanId(DateTime date);
    /// <summary>宿主当天实际使用的课表（预定课表优先）。</summary>
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);
    /// <summary>没有该日预定课表时宿主按规则匹配到的课表（临时课表群、轮换等规则与宿主一致）。</summary>
    ClassPlan? ResolveRulePlan(DateTime date, out Guid? planId);
    /// <summary>课表所用时间表的上下课时间签名；时间表变化后托管临时层需要按新时间表重建。</summary>
    string GetLayoutSignature(ClassPlan plan);
    /// <summary>以宿主规则复制来源课表为该日临时层，并让预定课表指向它。</summary>
    Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date);
    /// <summary>设置或（planId 为 null 时）删除该日预定课表，不删除任何课表。</summary>
    void SetOrderedPlan(DateTime date, Guid? planId);
    /// <summary>删除一张临时层课表；绝不删除管理员的源课表。</summary>
    void DeleteOverlayPlan(Guid planId);
    void SaveProfile();
}

public sealed class ClassIslandScheduleOverlayHost(ILessonsService lessons, IProfileService profiles) : IScheduleOverlayHost
{
    // ClassIsland 2.2 更换了这些字典的实现类，按接口反射读取以同时兼容新旧宿主。
    public IReadOnlyDictionary<Guid, ClassPlan> ClassPlans =>
        HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, ClassPlan>>(profiles.Profile, "ClassPlans");

    public bool SubjectExists(Guid subjectId) =>
        HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profiles.Profile, "Subjects").ContainsKey(subjectId);

    public Guid? GetOrderedPlanId(DateTime date) =>
        OrderedSchedules.TryGetValue(date.Date, out var schedule) ? schedule.ClassPlanId : null;

    public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => lessons.GetClassPlanByDate(date.Date, out planId);

    /// <summary>
    /// SDK 没有“忽略预定课表的规则匹配”接口：在 UI 线程上临时摘掉该日预定课表查询后立即放回，期间不保存，
    /// 以复用宿主完整的匹配规则（临时课表群、多周轮换偏移等），不必在插件里重新实现。
    /// </summary>
    public ClassPlan? ResolveRulePlan(DateTime date, out Guid? planId)
    {
        var ordered = OrderedSchedules;
        if (!ordered.TryGetValue(date.Date, out var schedule)) return lessons.GetClassPlanByDate(date.Date, out planId);
        ordered.Remove(date.Date);
        try
        {
            return lessons.GetClassPlanByDate(date.Date, out planId);
        }
        finally
        {
            ordered[date.Date] = schedule;
        }
    }

    public string GetLayoutSignature(ClassPlan plan)
    {
        if (plan.TimeLayout is not { } layout) return plan.TimeLayoutId.ToString();
        var text = new StringBuilder(plan.TimeLayoutId.ToString());
        foreach (var item in layout.Layouts)
            text.Append('|').Append(item.TimeType).Append(':').Append(item.StartTime).Append('-').Append(item.EndTime);
        return text.ToString();
    }

    public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date) =>
        profiles.CreateTempClassPlan(sourcePlanId, enableDateTime: date.Date);

    public void SetOrderedPlan(DateTime date, Guid? planId)
    {
        if (planId is { } id) OrderedSchedules[date.Date] = new OrderedSchedule { ClassPlanId = id };
        else OrderedSchedules.Remove(date.Date);
    }

    public void DeleteOverlayPlan(Guid planId)
    {
        var plans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(profiles.Profile, "ClassPlans");
        if (plans.TryGetValue(planId, out var plan) && plan.IsOverlay) plans.Remove(planId);
    }

    public void SaveProfile() => profiles.SaveProfile();

    private IDictionary<DateTime, OrderedSchedule> OrderedSchedules =>
        HostApiCompat.ReadProperty<IDictionary<DateTime, OrderedSchedule>>(profiles.Profile, "OrderedSchedules");
}
