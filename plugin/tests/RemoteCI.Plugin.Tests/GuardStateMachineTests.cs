using System.Reflection;
using RemoteCI.Guard;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class GuardStateMachineTests
{
    private const long Start = 1_000_000;

    private static GuardConfig Policy(bool restartAfterExit = true, int delayMinutes = 40, bool restartOnCrash = true, bool preventDeletion = true) => new()
    {
        Enabled = true,
        RestartAfterExit = restartAfterExit,
        RestartDelayMinutes = delayMinutes,
        RestartOnCrash = restartOnCrash,
        PreventDeletion = preventDeletion,
    };

    private static GuardObservation See(long now, bool running = false, bool present = true, string? state = null, long stateAt = Start) => new()
    {
        NowMs = now,
        Running = running,
        InstallPresent = present,
        RunState = state,
        RunStateAtMs = stateAt,
    };

    /// <summary>先看到一次运行，使状态机进入“已在运行”。</summary>
    private static GuardStateMachine Running(GuardConfig policy)
    {
        var machine = new GuardStateMachine();
        Assert.Equal(GuardAction.None, machine.Tick(See(Start, running: true), policy).Action);
        return machine;
    }

    [Fact]
    public void NormalExit_LaunchesAfterConfiguredDelay_WithExitRestartedEvent()
    {
        var policy = Policy(delayMinutes: 40);
        var machine = Running(policy);

        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 5_000, state: GuardRunStates.Exiting), policy).Action);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 39 * 60_000), policy).Action);
        var decision = machine.Tick(See(Start + 5_000 + 40 * 60_000), policy);

        Assert.Equal(GuardAction.Launch, decision.Action);
        Assert.Equal(GuardEventKinds.ExitRestarted, decision.EventKind);
        Assert.Equal("退出 40 分钟后自动启动", decision.Detail);
    }

    [Fact]
    public void NormalExit_IsIgnored_WhenRestartAfterExitIsOff()
    {
        var policy = Policy(restartAfterExit: false);
        var machine = Running(policy);

        machine.Tick(See(Start + 5_000, state: GuardRunStates.Exiting), policy);

        Assert.False(machine.HasPendingLaunch);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 24 * 60 * 60_000L), policy).Action);
    }

    [Fact]
    public void PendingExitLaunch_IsCancelled_WhenClassIslandStartsAgain()
    {
        var policy = Policy(delayMinutes: 1);
        var machine = Running(policy);
        machine.Tick(See(Start + 5_000, state: GuardRunStates.Exiting), policy);

        machine.Tick(See(Start + 30_000, running: true), policy);

        Assert.False(machine.HasPendingLaunch);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 5 * 60_000, running: true), policy).Action);
    }

    [Fact]
    public void ZeroDelay_StillWaitsTenSeconds_SoRestartsOwnRelaunchWins()
    {
        var policy = Policy(delayMinutes: 0);
        var machine = Running(policy);
        machine.Tick(See(Start + 5_000, state: GuardRunStates.Exiting), policy);

        Assert.Equal(Start + 5_000 + GuardStateMachine.MinExitRestartDelayMs, machine.PendingLaunchAt);
    }

    [Fact]
    public void UnexpectedExit_RestartsAfterFiveSeconds()
    {
        var policy = Policy();
        var machine = Running(policy);

        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 5_000, state: GuardRunStates.Running), policy).Action);
        var decision = machine.Tick(See(Start + 10_000), policy);

        Assert.Equal(GuardAction.Launch, decision.Action);
        Assert.Equal(GuardEventKinds.CrashRestarted, decision.EventKind);
    }

    [Fact]
    public void UnexpectedExit_IsIgnored_WhenRestartOnCrashIsOff()
    {
        var policy = Policy(restartOnCrash: false);
        var machine = Running(policy);

        machine.Tick(See(Start + 5_000, state: GuardRunStates.Running), policy);

        Assert.False(machine.HasPendingLaunch);
    }

    [Fact]
    public void RepeatedCrashes_PauseRestartsForTenMinutes()
    {
        var policy = Policy();
        var machine = Running(policy);
        var now = Start;
        for (var crash = 0; crash < GuardStateMachine.CrashLimit; crash++)
        {
            now += 5_000;
            machine.Tick(See(now, state: GuardRunStates.Running), policy);
            now += 5_000;
            Assert.Equal(GuardAction.Launch, machine.Tick(See(now), policy).Action);
            now += 5_000;
            machine.Tick(See(now, running: true), policy);
        }

        now += 5_000;
        var paused = machine.Tick(See(now, state: GuardRunStates.Running), policy);

        Assert.Equal(GuardAction.None, paused.Action);
        Assert.Equal(GuardEventKinds.CrashLoopPaused, paused.EventKind);
        Assert.Equal(GuardAction.None, machine.Tick(See(now + 60_000), policy).Action);
        Assert.Equal(GuardAction.Launch, machine.Tick(See(now + GuardStateMachine.CrashPauseMs), policy).Action);
    }

    [Fact]
    public void LaunchThatNeverComesUp_CountsAsCrash()
    {
        var policy = Policy();
        var machine = Running(policy);
        machine.Tick(See(Start + 5_000), policy);
        Assert.Equal(GuardAction.Launch, machine.Tick(See(Start + 10_000), policy).Action);

        machine.Tick(See(Start + 10_000 + GuardStateMachine.LaunchConfirmMs), policy);

        Assert.True(machine.HasPendingLaunch);
    }

    [Fact]
    public void Updating_DoesNotInterfere_UntilWaitExpires()
    {
        var policy = Policy();
        var machine = Running(policy);

        machine.Tick(See(Start + 5_000, state: GuardRunStates.Updating, stateAt: Start), policy);

        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 10 * 60_000), policy).Action);
        Assert.Equal(GuardAction.Launch, machine.Tick(See(Start + GuardStateMachine.UpdatingWaitMs), policy).Action);
    }

    [Fact]
    public void Deletion_IsConfirmedTwice_BeforeRestore()
    {
        var policy = Policy();
        var machine = Running(policy);

        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 5_000, present: false), policy).Action);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 10_000, present: false), policy).Action);
        var restore = machine.Tick(See(Start + 5_000 + GuardStateMachine.DeletionConfirmMs, present: false), policy);

        Assert.Equal(GuardAction.Restore, restore.Action);
        Assert.Equal(GuardEventKinds.Restored, restore.EventKind);
    }

    [Fact]
    public void Deletion_ThatReappears_IsNotRestored()
    {
        var policy = Policy();
        var machine = Running(policy);
        machine.Tick(See(Start + 5_000, present: false), policy);
        machine.Tick(See(Start + 10_000, running: true), policy);

        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 15_000, present: false), policy).Action);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 20_000, present: false), policy).Action);
    }

    [Fact]
    public void Deletion_IsIgnored_WhenPreventDeletionIsOff_AndRetriedLaterAfterFailedRestore()
    {
        var off = Policy(preventDeletion: false);
        var machine = Running(off);
        machine.Tick(See(Start + 5_000, present: false), off);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 60_000, present: false), off).Action);

        var on = Policy();
        machine.Tick(See(Start + 65_000, present: false), on);
        Assert.Equal(GuardAction.Restore, machine.Tick(See(Start + 80_000, present: false), on).Action);
        machine.RestoreFailed(Start + 80_000);
        machine.Tick(See(Start + 90_000, present: false), on);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 120_000, present: false), on).Action);
        machine.Tick(See(Start + 80_000 + GuardStateMachine.RestoreRetryMs, present: false), on);
        Assert.Equal(GuardAction.Restore,
            machine.Tick(See(Start + 80_000 + GuardStateMachine.RestoreRetryMs + GuardStateMachine.DeletionConfirmMs, present: false), on).Action);
    }

    [Fact]
    public void GuardStartup_LaunchesClassIslandAfterGracePeriod_WhenNotRunning()
    {
        var policy = Policy();
        var machine = new GuardStateMachine();

        Assert.Equal(GuardAction.None, machine.Tick(See(Start), policy).Action);
        Assert.Equal(GuardAction.None, machine.Tick(See(Start + 30_000), policy).Action);
        var launch = machine.Tick(See(Start + GuardStateMachine.StartupGraceMs), policy);

        Assert.Equal(GuardAction.Launch, launch.Action);
        Assert.Null(launch.EventKind);
    }

    [Fact]
    public void GuardEventKinds_MatchSharedProtocol()
    {
        static Dictionary<string, string?> Constants(Type type) => type
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .Where(x => x.IsLiteral)
            .ToDictionary(x => x.Name, x => (string?)x.GetRawConstantValue());

        Assert.Equal(Constants(typeof(AppGuardEventKinds)).OrderBy(x => x.Key), Constants(typeof(GuardEventKinds)).OrderBy(x => x.Key));
    }
}
