using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 课表写操作的最小防腐层：换课流程所需的 Profile 写能力
/// （ClassIsland 的 IProfileService 含 internal 成员，外部程序集无法实现假对象）。
/// </summary>
public interface IProfileWriteOperations
{
    IReadOnlyDictionary<Guid, Subject> Subjects { get; }
    IReadOnlyDictionary<Guid, ClassPlan> ClassPlans { get; }
    Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime? enableDateTime = null);
    void SaveProfile();
}

public sealed class ProfileWriteAdapter(IProfileService profiles) : IProfileWriteOperations
{
    // 同 ScheduleBackendAdapter：这两个字典的返回类型在 ClassIsland 2.2 中已更换实现类。
    public IReadOnlyDictionary<Guid, Subject> Subjects =>
        HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, Subject>>(profiles.Profile, "Subjects");
    public IReadOnlyDictionary<Guid, ClassPlan> ClassPlans =>
        HostApiCompat.ReadProperty<IReadOnlyDictionary<Guid, ClassPlan>>(profiles.Profile, "ClassPlans");
    public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime? enableDateTime = null) =>
        profiles.CreateTempClassPlan(sourcePlanId, enableDateTime: enableDateTime);
    public void SaveProfile() => profiles.SaveProfile();
}
