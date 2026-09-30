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
    ScheduleBundle? GetLatestSchedule(Guid classId);
    void SaveEvent(Guid classId, ClassEvent @event);
    ClassEvent? GetLatestEvent(Guid classId);
    void SaveExtensions(Guid classId, IReadOnlyList<ExtensionDefinition> extensions);
    IReadOnlyList<ExtensionDefinition>? GetLatestExtensions(Guid classId);
}
