using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ScheduleSyncTaskTrackerTests
{
    [Fact]
    public async Task ActiveTask_ReturnsBusyAndCompletionReleasesWaiter()
    {
        var tracker = new ScheduleSyncTaskTracker();
        var first = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.WebUi), TestWebApplicationFactory.DefaultClassId);
        var second = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Automatic), TestWebApplicationFactory.DefaultClassId);

        Assert.Equal(ScheduleSyncTaskState.Running, first.State);
        Assert.Equal(ScheduleSyncTaskState.Busy, second.State);
        Assert.Equal(first.TaskId, second.ActiveTaskId);

        var waiting = tracker.WaitForTerminalAsync(first.TaskId, TimeSpan.FromSeconds(1));
        tracker.Observe(new ScheduleSyncStatus
        {
            TaskId = first.TaskId,
            Source = first.Source,
            State = ScheduleSyncTaskState.Completed,
            Message = "完成",
            StartedAt = first.StartedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            ClassId = TestWebApplicationFactory.DefaultClassId,
        });

        Assert.Equal(ScheduleSyncTaskState.Completed, (await waiting).State);
        Assert.Null(tracker.Current(TestWebApplicationFactory.DefaultClassId));
        // 其他班级的任务互不影响：本班结束后，新班级可立即启动自己的课表任务。
        var otherClass = Guid.NewGuid();
        Assert.Equal(
            ScheduleSyncTaskState.Running,
            tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Watch), otherClass).State);
    }
}
