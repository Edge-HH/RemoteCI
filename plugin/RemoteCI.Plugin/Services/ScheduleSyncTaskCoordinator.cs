using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 插件端课表同步全局闸门：云端、局域网、自动拉取和插件按钮最终都在这里竞争同一个任务。
/// </summary>
public sealed class ScheduleSyncTaskCoordinator
{
    private readonly object _gate = new();
    private ScheduleSyncStatus? _current;

    public event Action<ScheduleSyncStatus>? StatusChanged;

    public ScheduleSyncStatus? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public ScheduleSyncStatus TryStart(ScheduleSyncRequest request)
    {
        ScheduleSyncStatus status;
        lock (_gate)
        {
            if (_current is { State: ScheduleSyncTaskState.Running } active)
            {
                status = new ScheduleSyncStatus
                {
                    TaskId = request.TaskId,
                    Source = request.Source,
                    State = ScheduleSyncTaskState.Busy,
                    Message = $"已有{SourceName(active.Source)}课表任务正在执行，请稍候",
                    StartedAt = active.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    ActiveTaskId = active.TaskId,
                };
            }
            else
            {
                status = new ScheduleSyncStatus
                {
                    TaskId = request.TaskId,
                    Source = request.Source,
                    State = ScheduleSyncTaskState.Running,
                    Message = $"正在执行{SourceName(request.Source)}课表任务",
                    StartedAt = DateTimeOffset.UtcNow,
                };
                _current = status;
            }
        }

        StatusChanged?.Invoke(status);
        return status;
    }

    public bool TryComplete(string taskId, string message, out ScheduleSyncStatus status) =>
        TryFinish(taskId, ScheduleSyncTaskState.Completed, message, out status);

    public bool TryFail(string taskId, string message, out ScheduleSyncStatus status) =>
        TryFinish(taskId, ScheduleSyncTaskState.Failed, message, out status);

    private bool TryFinish(string taskId, ScheduleSyncTaskState state, string message, out ScheduleSyncStatus status)
    {
        lock (_gate)
        {
            if (_current is not { State: ScheduleSyncTaskState.Running } active || active.TaskId != taskId)
            {
                status = new ScheduleSyncStatus();
                return false;
            }

            status = new ScheduleSyncStatus
            {
                TaskId = active.TaskId,
                Source = active.Source,
                State = state,
                Message = message,
                StartedAt = active.StartedAt,
                FinishedAt = DateTimeOffset.UtcNow,
            };
            _current = null;
        }

        StatusChanged?.Invoke(status);
        return true;
    }

    internal static string SourceName(ScheduleSyncSource source) => source switch
    {
        ScheduleSyncSource.Plugin => "插件端推送",
        ScheduleSyncSource.WebUi => "WebUI 拉取",
        ScheduleSyncSource.Watch => "手表端拉取",
        ScheduleSyncSource.Automatic => "自动拉取",
        ScheduleSyncSource.Connection => "连接初始化拉取",
        ScheduleSyncSource.Mobile => "手机端拉取",
        _ => "远端拉取",
    };
}

