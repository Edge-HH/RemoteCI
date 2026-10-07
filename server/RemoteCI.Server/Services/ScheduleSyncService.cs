using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 服务端课表任务编排：在 WebUI、云端手表、自动任务和连接初始化之间按班级共享运行状态，
/// 插件端仍执行最终互斥，以覆盖局域网直连和插件本地按钮。
/// </summary>
public sealed class ScheduleSyncService(
    ScheduleSyncTaskTracker tracker,
    PeerRegistry peers,
    ILogger<ScheduleSyncService> logger)
{
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(15);
    private readonly Dictionary<Guid, CancellationTokenSource> _timeouts = new();

    public ScheduleSyncStatus? Current(Guid classId) => tracker.Current(classId);

    public Task<ScheduleSyncStatus> StartAsync(
        ScheduleSyncSource source, Guid classId, CancellationToken ct = default, string? taskId = null)
    {
        var request = ScheduleSyncRequest.Create(source, taskId);
        request.ClassId = classId;
        return StartCoreAsync(request, null, ct);
    }

    public async Task<ScheduleSyncStatus> StartFromPluginAsync(
        Guid pluginConnectionId, Guid classId, ScheduleSyncSource source, CancellationToken ct = default)
    {
        // 新插件接入拉取直接定向发送；插件端全局闸门负责最终互斥，
        // 避免握手阶段尚未回传 Running 状态时把其他真实请求误判为重复。
        var request = ScheduleSyncRequest.Create(source);
        request.ClassId = classId;
        var sent = await peers.RequestSchedulePullFromAsync(pluginConnectionId, request, ct);
        return new ScheduleSyncStatus
        {
            TaskId = request.TaskId,
            Source = source,
            State = sent ? ScheduleSyncTaskState.Running : ScheduleSyncTaskState.Failed,
            Message = sent ? "已请求新连接插件同步课表" : "插件连接已失效，无法同步课表",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = sent ? null : DateTimeOffset.UtcNow,
            ClassId = classId,
        };
    }

    public async Task CompleteFromScheduleAsync(Guid classId, CancellationToken ct = default)
    {
        if (tracker.Current(classId) is not { } running) return;
        await ObserveAndPublishAsync(new ScheduleSyncStatus
        {
            TaskId = running.TaskId,
            Source = running.Source,
            State = ScheduleSyncTaskState.Completed,
            Message = "课表已生成并推送完成",
            StartedAt = running.StartedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            ClassId = classId,
        }, ct);
    }

    public async Task FailActiveAsync(string message, Guid classId, CancellationToken ct = default)
    {
        if (tracker.Current(classId) is not { } running) return;
        await ObserveAndPublishAsync(Failure(running, message), ct);
    }

    public async Task<ScheduleSyncStatus> StartAndWaitAsync(
        ScheduleSyncSource source, Guid classId, CancellationToken ct = default)
    {
        var initial = await StartAsync(source, classId, ct);
        if (initial.State != ScheduleSyncTaskState.Running) return initial;
        try
        {
            return await tracker.WaitForTerminalAsync(initial.TaskId, TaskTimeout + TimeSpan.FromSeconds(1), ct);
        }
        catch (TimeoutException)
        {
            var failed = Failure(initial, "等待插件返回课表超时，请检查插件连接和 ClassIsland 课表");
            await ObserveAndPublishAsync(failed, ct);
            return failed;
        }
    }

    public async Task ObserveFromPluginAsync(ScheduleSyncStatus status, CancellationToken ct = default)
    {
        logger.LogInformation(
            "插件课表任务状态：{TaskId} {Source} {State} - {Message}",
            status.TaskId, status.Source, status.State, status.Message);
        tracker.Observe(status);
        var classId = status.ClassId ?? Classroom.DefaultId;
        if (status.State == ScheduleSyncTaskState.Running) ArmTimeout(status, classId);
        else if (status.State == ScheduleSyncTaskState.Busy && tracker.Current(classId) is { } active) ArmTimeout(active, classId);
        else if (status.State is ScheduleSyncTaskState.Completed or ScheduleSyncTaskState.Failed) CancelTimeout(classId);
        await peers.SendScheduleSyncStatusToWatchesAsync(status, ct);
    }

    private async Task<ScheduleSyncStatus> StartCoreAsync(
        ScheduleSyncRequest request, Guid? pluginConnectionId, CancellationToken ct)
    {
        var classId = request.ClassId ?? Classroom.DefaultId;
        var initial = tracker.TryBegin(request, classId);
        if (initial.State == ScheduleSyncTaskState.Busy)
        {
            await peers.SendScheduleSyncStatusToWatchesAsync(initial, ct);
            return initial;
        }

        await peers.SendScheduleSyncStatusToWatchesAsync(initial, ct);
        var sent = pluginConnectionId is { } connectionId
            ? await peers.RequestSchedulePullFromAsync(connectionId, request, ct)
            : await peers.RequestSchedulePullAsync(request, ct);
        if (!sent)
        {
            var failed = Failure(initial, "插件未在线，无法执行课表任务");
            await ObserveAndPublishAsync(failed, ct);
            return failed;
        }

        ArmTimeout(initial, classId);
        return initial;
    }

    private async Task ObserveAndPublishAsync(ScheduleSyncStatus status, CancellationToken ct)
    {
        tracker.Observe(status);
        if (status.State is ScheduleSyncTaskState.Completed or ScheduleSyncTaskState.Failed)
            CancelTimeout(status.ClassId ?? Classroom.DefaultId);
        await peers.SendScheduleSyncStatusToWatchesAsync(status, ct);
    }

    private void ArmTimeout(ScheduleSyncStatus running, Guid classId)
    {
        CancelTimeout(classId);
        var timeout = new CancellationTokenSource();
        var token = timeout.Token;
        _timeouts[classId] = timeout;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TaskTimeout, token);
                if (tracker.Current(classId)?.TaskId != running.TaskId) return;
                var failed = Failure(running, "课表任务执行超时，请检查插件日志和网络连接");
                await ObserveAndPublishAsync(failed, CancellationToken.None);
                logger.LogWarning("班级 {ClassId} 课表任务 {TaskId} 执行超时，来源 {Source}",
                    classId, running.TaskId, running.Source);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 任务已进入终态。
            }
        });
    }

    private void CancelTimeout(Guid classId)
    {
        if (!_timeouts.Remove(classId, out var timeout)) return;
        timeout.Cancel();
        timeout.Dispose();
    }

    private static ScheduleSyncStatus Failure(ScheduleSyncStatus running, string message) => new()
    {
        TaskId = running.TaskId,
        Source = running.Source,
        State = ScheduleSyncTaskState.Failed,
        Message = message,
        StartedAt = running.StartedAt,
        FinishedAt = DateTimeOffset.UtcNow,
        ClassId = running.ClassId,
    };
}
