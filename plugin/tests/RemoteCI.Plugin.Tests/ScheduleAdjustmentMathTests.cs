using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScheduleAdjustmentMathTests
{
    private static readonly Guid Math = Guid.NewGuid();
    private static readonly Guid Physics = Guid.NewGuid();
    private static readonly Guid English = Guid.NewGuid();
    private static readonly Guid Chemistry = Guid.NewGuid();
    private static readonly HashSet<Guid> Known = [Math, Physics, English, Chemistry];

    [Fact]
    public void Compose_AppliesServerItemsWhoseGuardMatchesBase()
    {
        var id = Guid.NewGuid();
        var result = ScheduleAdjustmentMath.Compose([Math, Physics, English],
            [Server(id, 1, Physics, Chemistry)], [], Known.Contains);

        Assert.Equal([Math, Chemistry, English], result.Subjects);
        Assert.Equal(id, result.AppliedIds[1]);
        Assert.Equal(ScheduleAdjustmentItemState.Applied, Assert.Single(result.Results).State);
    }

    [Fact]
    public void Compose_ReportsConflictWhenBaseChanged()
    {
        var id = Guid.NewGuid();
        var result = ScheduleAdjustmentMath.Compose([Math, English],
            [Server(id, 1, Physics, Chemistry)], [], Known.Contains);

        Assert.Equal([Math, English], result.Subjects);
        var conflict = Assert.Single(result.Results);
        Assert.Equal(ScheduleAdjustmentItemState.Conflict, conflict.State);
        Assert.Equal(ScheduleAdjustmentConflictCodes.BaseChanged, conflict.Code);
        Assert.Empty(result.AppliedIds);
    }

    [Fact]
    public void Compose_ReportsOutOfRangeAndMissingSubjects()
    {
        var missing = Guid.NewGuid();
        var result = ScheduleAdjustmentMath.Compose([Math, Physics],
            [Server(Guid.NewGuid(), 5, Math, Physics), Server(Guid.NewGuid(), 0, Math, missing)], [], Known.Contains);

        Assert.Equal([ScheduleAdjustmentConflictCodes.IndexOutOfRange, ScheduleAdjustmentConflictCodes.SubjectMissing],
            result.Results.Select(x => x.Code));
        Assert.Equal([Math, Physics], result.Subjects);
    }

    [Fact]
    public void Compose_LocalItemsSitAboveServerItems()
    {
        var server = Guid.NewGuid();
        var local = Guid.NewGuid();
        var result = ScheduleAdjustmentMath.Compose([Math, Physics],
            [Server(server, 0, Math, English)], [Local(local, 0, Math, Chemistry)], Known.Contains);

        Assert.Equal([Chemistry, Physics], result.Subjects);
        Assert.Equal(local, result.AppliedIds[0]);
        // 服务端条目本身仍算已应用：它在本地条目之下，被收编后由服务端决定最终顺序。
        Assert.Equal(ScheduleAdjustmentItemState.Applied, Assert.Single(result.Results).State);
    }

    [Fact]
    public void Compose_SkipsLocalItemsWithStaleGuard()
    {
        var result = ScheduleAdjustmentMath.Compose([Math, Physics], [], [Local(Guid.NewGuid(), 1, English, Chemistry)], Known.Contains);

        Assert.Equal([Math, Physics], result.Subjects);
        Assert.Empty(result.AppliedIds);
    }

    [Fact]
    public void FromChangeRequest_ExchangeUsesBaseSubjectsAsGuards()
    {
        // 第 0 节已被调成化学（基础为数学），再与第 2 节英语互换。
        var effective = new[] { Chemistry, Physics, English };
        var baseSubjects = new[] { Math, Physics, English };
        var changes = ScheduleAdjustmentMath.FromChangeRequest(new ScheduleChangeRequest
        {
            Mode = ScheduleChangeMode.Exchange,
            SourceIndex = 0,
            TargetIndex = 2,
        }, effective, baseSubjects);

        Assert.Equal([(0, Math, English), (2, English, Chemistry)], changes);
    }

    [Fact]
    public void FromChangeRequest_IgnoresNoOpReplace()
    {
        var changes = ScheduleAdjustmentMath.FromChangeRequest(new ScheduleChangeRequest
        {
            Mode = ScheduleChangeMode.Replace,
            SourceIndex = 1,
            ReplacementSubjectId = Physics,
        }, [Math, Physics], [Math, Physics]);

        Assert.Empty(changes);
    }

    [Fact]
    public void FromChangeRequest_ReplaceBackToBaseIsKeptAsExplicitItem()
    {
        var changes = ScheduleAdjustmentMath.FromChangeRequest(new ScheduleChangeRequest
        {
            Mode = ScheduleChangeMode.Replace,
            SourceIndex = 0,
            ReplacementSubjectId = Math,
        }, [Chemistry, Physics], [Math, Physics]);

        Assert.Equal([(0, Math, Math)], changes);
    }

    private static ScheduleSlotChange Server(Guid id, int index, Guid from, Guid to) =>
        new() { Id = id, Date = "2026-10-26", Index = index, FromSubjectId = from, ToSubjectId = to };

    private static LocalScheduleAdjustment Local(Guid id, int index, Guid from, Guid to) =>
        new() { Id = id, Date = "2026-10-26", Index = index, FromSubjectId = from, ToSubjectId = to };
}
