using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 内存状态缓存：按班级分桶保存各插件推送的最新快照与最近事件，供新连接/HTTP 查询获取。
/// </summary>
public interface IStateStore
{
    void SaveSnapshot(Guid classId, ClassStateSnapshot snapshot);
    ClassStateSnapshot? GetLatestSnapshot(Guid classId);
    void SaveSchedule(Guid classId, ScheduleBundle schedule);
    /// <summary>最新课表，已叠加换课申请产生的单节临时任课老师覆盖；对外展示与个人日程都用它。</summary>
    ScheduleBundle? GetLatestSchedule(Guid classId);
    /// <summary>插件原样推送的课表（不叠加临时任课老师），用于班级授权等需要稳定绑定的场景。</summary>
    ScheduleBundle? GetSourceSchedule(Guid classId);
    void SaveEvent(Guid classId, ClassEvent @event);
    ClassEvent? GetLatestEvent(Guid classId);
    void SaveExtensions(Guid classId, IReadOnlyList<ExtensionDefinition> extensions);
    IReadOnlyList<ExtensionDefinition>? GetLatestExtensions(Guid classId);
    void SaveExtensionGroups(Guid classId, IReadOnlyList<ExtensionGroupDefinition> groups);
    IReadOnlyList<ExtensionGroupDefinition>? GetLatestExtensionGroups(Guid classId);
}
