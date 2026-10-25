using System.Collections.Concurrent;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>向教室端下发调课集合的接缝；生产走 PeerRegistry，测试可替换为假插件。</summary>
public interface IScheduleAdjustmentChannel
{
    /// <summary>班级插件在线时返回是否声明了 schedule.adjustments 能力；离线时返回 null。</summary>
    bool? Supports(Guid classId);

    /// <summary>下发全量调课集合（期望状态，可重复发送）；回执随插件下一次课表推送到达。</summary>
    Task<bool> PushAsync(Guid classId, ScheduleAdjustmentSet set, CancellationToken ct);
}

public sealed class PeerScheduleAdjustmentChannel(PeerRegistry peers) : IScheduleAdjustmentChannel
{
    public bool? Supports(Guid classId) => peers.HasPluginFor(classId)
        ? peers.PrimaryPluginSupports(classId, RemoteCiCapabilities.ScheduleAdjustments)
        : null;

    public Task<bool> PushAsync(Guid classId, ScheduleAdjustmentSet set, CancellationToken ct) =>
        peers.SendToPluginAsync(classId, Envelope.ScheduleAdjustments(set), ct);
}

/// <summary>
/// 等待插件回执某个调课集合版本。回执经插件的课表推送到达（WebSocket 接收循环），
/// 所以审批等需要即时结果的流程在这里等待，而不是在接收循环里同步等待命令结果。
/// </summary>
public sealed class ScheduleAdjustmentAckWaiter
{
    private readonly ConcurrentDictionary<Guid, long> _acked = new();
    private readonly ConcurrentDictionary<Guid, List<(long Version, TaskCompletionSource Source)>> _waiters = new();

    public void Acknowledge(Guid classId, long version)
    {
        _acked.AddOrUpdate(classId, version, (_, current) => Math.Max(current, version));
        if (!_waiters.TryGetValue(classId, out var list)) return;
        List<TaskCompletionSource> done;
        lock (list)
        {
            done = list.Where(x => x.Version <= version).Select(x => x.Source).ToList();
            list.RemoveAll(x => x.Version <= version);
        }
        foreach (var source in done) source.TrySetResult();
    }

    /// <summary>插件回执了不低于 <paramref name="version"/> 的版本时返回 true，超时返回 false。</summary>
    public async Task<bool> WaitAsync(Guid classId, long version, TimeSpan timeout, CancellationToken ct)
    {
        if (_acked.TryGetValue(classId, out var acked) && acked >= version) return true;
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _waiters.GetOrAdd(classId, _ => []);
        lock (list) list.Add((version, source));
        // 登记前后可能刚好收到回执，再检查一次。
        if (_acked.TryGetValue(classId, out acked) && acked >= version) Acknowledge(classId, acked);
        try
        {
            await source.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            lock (list) list.RemoveAll(x => ReferenceEquals(x.Source, source));
            return false;
        }
    }
}
