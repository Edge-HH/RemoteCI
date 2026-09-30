using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class TeacherComingWorkflowTests
{
    [Fact]
    public async Task Workflow_ShowsWaitsOneSecondAndClearsInPlugin()
    {
        var steps = new List<string>();

        var result = await CommandHandler.RunTeacherComingWorkflowAsync(
            () =>
            {
                steps.Add("show");
                return Task.FromResult(Success("shown"));
            },
            () =>
            {
                steps.Add("clear");
                return Task.FromResult(Success("cleared"));
            },
            duration =>
            {
                steps.Add($"delay:{duration.TotalSeconds:0}");
                return Task.CompletedTask;
            });

        Assert.True(result.Success);
        Assert.Equal(["show", "delay:1", "clear"], steps);
        Assert.Equal("已显示“老师来了”并自动清除", result.Message);
    }

    [Fact]
    public async Task Workflow_DoesNotWaitOrClearWhenShowFails()
    {
        var clearCalled = false;
        var delayCalled = false;
        var failure = CommandResult.Failure("SHOW_FAILED", "显示失败");

        var result = await CommandHandler.RunTeacherComingWorkflowAsync(
            () => Task.FromResult(failure),
            () =>
            {
                clearCalled = true;
                return Task.FromResult(Success("cleared"));
            },
            _ =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        Assert.Same(failure, result);
        Assert.False(delayCalled);
        Assert.False(clearCalled);
    }

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = "OK",
        Message = message,
    };
}
