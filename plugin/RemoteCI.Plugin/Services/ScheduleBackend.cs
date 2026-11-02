using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 课表读取的最小防腐层：隔离 ClassIsland 服务接口（其含 internal 成员，外部程序集无法实现假对象），
/// 让课表构建与修订号逻辑可单元测试。生产由 ScheduleBackendAdapter 适配真实服务。
/// </summary>
public interface IScheduleBackend
{
    IReadOnlyDictionary<Guid, Subject> Subjects { get; }
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);

    /// <summary>课表所用时间表各时间点的类型（0 上课、1 课间、2 分割线、3 行动），按时间顺序；读不到时返回 null。</summary>
    IReadOnlyList<int>? GetTimeTypes(ClassPlan plan) => null;
}

public sealed class ScheduleBackendAdapter(ILessonsService lessons, IProfileService profiles) : IScheduleBackend
{
    // ClassIsland 2.2 把 Subjects 的返回类型从 ObservableDictionary 换成 ObservableOrderedDictionary，
    // 两者都实现 IReadOnlyDictionary，这里按接口反射读取以同时兼容新旧宿主。
    public IReadOnlyDictionary<Guid, Subject> Subjects =>
        HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profiles.Profile, "Subjects");
    public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => lessons.GetClassPlanByDate(date, out planId);

    public IReadOnlyList<int>? GetTimeTypes(ClassPlan plan)
    {
        try
        {
            return plan.TimeLayout?.Layouts.Select(item => item.TimeType).ToList();
        }
        catch (Exception)
        {
            // 时间表引用失效等宿主内部异常：不上报分割线，课表其余部分照常。
            return null;
        }
    }
}
