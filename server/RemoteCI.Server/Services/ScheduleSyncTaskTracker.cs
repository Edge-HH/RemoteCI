using System.Collections.Concurrent;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>服务端按班级保存当前课表任务并为 WebUI 提供可等待的终态。</summary>
public sealed class ScheduleSyncTaskTracker
{
    private readonly ConcurrentDictionary<Guid, ScheduleSyncStatus> _current = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ScheduleSyncStatus>> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScheduleSyncStatus> _terminal = new(StringComparer.Ordinal);

    public ScheduleSyncStatus? Current(Guid classId) =>
        _current.TryGetValue(classId, out var status) ? status : null;

    public ScheduleSyncStatus TryBegin(ScheduleSyncRequest request, Guid classId)
    {
        if (_current.TryGetValue(classId, out var active) && active.State == ScheduleSyncTaskState.Running)
        {
            return new ScheduleSyncStatus
            {
                TaskId = request.TaskId,
                Source = request.Source,
                State = ScheduleSyncTaskState.Busy,
                Message = $"已有{SourceName(active.Source)}课表任务正在执行，请稍候",
                StartedAt = active.StartedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                ActiveTaskId = active.TaskId,
                ClassId = classId,
            };
        }

        var started = new ScheduleSyncStatus
        {
            TaskId = request.TaskId,
            Source = request.Source,
            State = ScheduleSyncTaskState.Running,
            Message = $"正在连接插件执行{SourceName(request.Source)}任务",
            StartedAt = DateTimeOffset.UtcNow,
            ClassId = classId,
        };
        _current[classId] = started;
        return started;
    }

    public void Observe(ScheduleSyncStatus status)
    {
        var classId = status.ClassId ?? Classroom.DefaultId;
        if (status.State == ScheduleSyncTaskState.Running)
        {
            _current[classId] = status;
        }
        else if (_current.TryGetValue(classId, out var current) && current.TaskId == status.TaskId)
        {
            if (status.State == ScheduleSyncTaskState.Busy && !string.IsNullOrWhiteSpace(status.ActiveTaskId))
            {
                _current[classId] = new ScheduleSyncStatus
                {
                    TaskId = status.ActiveTaskId,
                    Source = ScheduleSyncSource.Unknown,
                    State = ScheduleSyncTaskState.Running,
                    Message = status.Message,
                    StartedAt = status.StartedAt,
                    ClassId = classId,
                };
            }
            else
            {
                _current.TryRemove(classId, out _);
            }
        }

        if (status.State is ScheduleSyncTaskState.Completed or ScheduleSyncTaskState.Failed or ScheduleSyncTaskState.Busy)
        {
            RememberTerminal(status);
            if (_waiters.TryRemove(status.TaskId, out var waiter)) waiter.TrySetResult(status);
        }
    }

    private void RememberTerminal(ScheduleSyncStatus status)
    {
        _terminal[status.TaskId] = status;
        if (_terminal.Count <= 64) return;
        foreach (var stale in _terminal.Values
                     .OrderBy(x => x.FinishedAt ?? DateTimeOffset.MaxValue)
                     .Take(_terminal.Count - 64))
            _terminal.TryRemove(stale.TaskId, out _);
    }

    public async Task<ScheduleSyncStatus> WaitForTerminalAsync(
        string taskId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_terminal.TryRemove(taskId, out var completed)) return completed;
        var waiter = _waiters.GetOrAdd(taskId, _ =>
            new TaskCompletionSource<ScheduleSyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_terminal.TryRemove(taskId, out completed)) waiter.TrySetResult(completed);
        try
        {
            return await waiter.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _waiters.TryRemove(taskId, out _);
            _terminal.TryRemove(taskId, out _);
        }
    }

    internal static string SourceName(ScheduleSyncSource source) => source switch
    {
        ScheduleSyncSource.Plugin => "插件端推送",
        ScheduleSyncSource.WebUi => "WebUI 拉取",
        ScheduleSyncSource.Watch => "手表端拉取",
        ScheduleSyncSource.Automatic => "自动拉取",
        ScheduleSyncSource.Connection => "连接初始化拉取",
        _ => "课表同步",
    };
}
