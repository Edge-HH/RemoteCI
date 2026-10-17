using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 内存状态缓存：按班级分桶保存各插件推送的最新快照与最近事件，供新连接/HTTP 查询获取。
/// 课表与扩展声明另由 <see cref="ClassStateCacheService"/> 持久化，服务端重启后即可恢复。
/// </summary>
public interface IStateStore
{
    void SaveSnapshot(Guid classId, ClassStateSnapshot snapshot);
    ClassStateSnapshot? GetLatestSnapshot(Guid classId);
    /// <summary>所有班级中最近一份带有效时区偏移（±14 小时内）的状态快照所上报的教室端时区偏移（分钟）；没有时为 null。</summary>
    int? GetLatestTimeZoneOffsetMinutes();
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
