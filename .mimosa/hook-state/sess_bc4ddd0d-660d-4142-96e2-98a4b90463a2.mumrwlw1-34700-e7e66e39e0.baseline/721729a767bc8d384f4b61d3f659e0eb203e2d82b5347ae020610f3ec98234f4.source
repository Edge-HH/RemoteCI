using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScheduleSyncTaskCoordinatorTests
{
    [Fact]
    public void RunningTask_RejectsDuplicateAndReleasesAfterCompletion()
    {
        var coordinator = new ScheduleSyncTaskCoordinator();
        var firstRequest = ScheduleSyncRequest.Create(ScheduleSyncSource.WebUi);
        var secondRequest = ScheduleSyncRequest.Create(ScheduleSyncSource.Watch);

        var first = coordinator.TryStart(firstRequest);
        var second = coordinator.TryStart(secondRequest);

        Assert.Equal(ScheduleSyncTaskState.Running, first.State);
        Assert.Equal(ScheduleSyncTaskState.Busy, second.State);
        Assert.Equal(first.TaskId, second.ActiveTaskId);
        Assert.True(coordinator.TryComplete(first.TaskId, "课表推送完成", out var completed));
        Assert.Equal(ScheduleSyncTaskState.Completed, completed.State);

        var third = coordinator.TryStart(ScheduleSyncRequest.Create(ScheduleSyncSource.Plugin));
        Assert.Equal(ScheduleSyncTaskState.Running, third.State);
    }

    [Fact]
    public void FailedTask_ReleasesGateAndPublishesTerminalState()
    {
        var coordinator = new ScheduleSyncTaskCoordinator();
        var statuses = new List<ScheduleSyncStatus>();
        coordinator.StatusChanged += statuses.Add;
        var request = ScheduleSyncRequest.Create(ScheduleSyncSource.Automatic);

        var running = coordinator.TryStart(request);
        Assert.True(coordinator.TryFail(running.TaskId, "无法连接服务端", out var failed));

        Assert.Equal(new[] { ScheduleSyncTaskState.Running, ScheduleSyncTaskState.Failed }, statuses.Select(x => x.State));
        Assert.Equal("无法连接服务端", failed.Message);
        Assert.Null(coordinator.Current);
    }
}