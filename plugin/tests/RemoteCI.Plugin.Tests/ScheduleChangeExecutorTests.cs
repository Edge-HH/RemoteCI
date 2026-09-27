using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScheduleChangeExecutorTests
{
    private static readonly DateTime TestDay = new(2026, 8, 14);

    private sealed class FakeBackend : IScheduleBackend
    {
        public Dictionary<Guid, Subject> Subjects { get; } = [];
        public ClassPlan? Plan { get; set; }
        public ClassPlan? PlanAfterCreation { get; set; }
        public Guid? PlanId { get; set; }
        public Guid? PlanIdAfterCreation { get; set; }
        private int _calls;
        IReadOnlyDictionary<Guid, Subject> IScheduleBackend.Subjects => Subjects;
        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            var first = _calls++ == 0;
            planId = first ? PlanId : (PlanIdAfterCreation ?? PlanId);
            return first ? Plan : (PlanAfterCreation ?? Plan);
        }
    }

    private sealed class FakeProfileOps : IProfileWriteOperations
    {
        public Dictionary<Guid, Subject> Subjects { get; } = [];
        public Dictionary<Guid, ClassPlan> ClassPlans { get; } = [];
        public Guid? NextOverlayId { get; set; }
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }
        IReadOnlyDictionary<Guid, Subject> IProfileWriteOperations.Subjects => Subjects;
        IReadOnlyDictionary<Guid, ClassPlan> IProfileWriteOperations.ClassPlans => ClassPlans;
        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime? enableDateTime = null) => NextOverlayId;
        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }
    }

    private static (ScheduleCatalog Catalog, FakeBackend Backend, FakeProfileOps Profile, ClassPlan Plan, List<Guid> SubjectIds)
        CreateHarness(int courseCount = 2, bool overlay = false)
    {
        var backend = new FakeBackend();
        var profile = new FakeProfileOps();
        var subjectIds = Enumerable.Range(0, courseCount).Select(_ => Guid.NewGuid()).ToList();
        var plan = new ClassPlan { Name = "主课表", IsOverlay = overlay };
        for (var i = 0; i < courseCount; i++)
        {
            plan.Classes.Add(new ClassInfo { SubjectId = subjectIds[i], IsEnabled = true });
            var subject = new Subject { Name = $"科目{i}" };
            profile.Subjects[subjectIds[i]] = subject;
            backend.Subjects[subjectIds[i]] = subject;
        }
        backend.Plan = plan;
        backend.PlanId = Guid.NewGuid();
        return (new ScheduleCatalog(backend), backend, profile, plan, subjectIds);
    }

    private static ScheduleChangeRequest Exchange(string revision, int source = 0, int target = 1) => new()
    {
        Date = "2026-08-14",
        Mode = ScheduleChangeMode.Exchange,
        SourceIndex = source,
        TargetIndex = target,
        ExpectedRevision = revision,
    };

    [Fact]
    public void Apply_StaleRevisionReturnsScheduleStaleWithLatestRevision()
    {
        var (catalog, backend, profile, _, _) = CreateHarness();

        var result = ScheduleChangeExecutor.Apply(TestDay, Exchange("wrong-revision"), catalog, backend, profile);

        Assert.Equal(CommandResultCodes.ScheduleStale, result.Code);
        Assert.Equal(catalog.BuildDay(TestDay).Revision, result.ScheduleRevision);
        Assert.Equal(0, profile.SaveCount);
    }

    [Fact]
    public void Apply_MissingPlanReturnsScheduleUnavailable()
    {
        var (catalog, backend, profile, _, _) = CreateHarness();
        backend.Plan = null;

        var result = ScheduleChangeExecutor.Apply(TestDay, Exchange("rev"), catalog, backend, profile);

        Assert.Equal(CommandResultCodes.ScheduleUnavailable, result.Code);
        Assert.Contains("没有可编辑课表", result.Message);
    }

    [Fact]
    public void Apply_ReplacementWithUnknownSubjectIsRejected()
    {
        var (catalog, backend, profile, _, _) = CreateHarness(overlay: true);
        var request = new ScheduleChangeRequest
        {
            Date = "2026-08-14",
            Mode = ScheduleChangeMode.Replace,
            SourceIndex = 0,
            ReplacementSubjectId = Guid.NewGuid(),
            ExpectedRevision = catalog.BuildDay(TestDay).Revision,
        };

        var result = ScheduleChangeExecutor.Apply(TestDay, request, catalog, backend, profile);

        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
        Assert.Contains("替换科目不存在", result.Message);
        Assert.Equal(0, profile.SaveCount);
    }

    [Fact]
    public void Apply_ExchangeOutOfRangeTargetIsRejected()
    {
        var (catalog, backend, profile, _, _) = CreateHarness(overlay: true);
        var revision = catalog.BuildDay(TestDay).Revision;

        var result = ScheduleChangeExecutor.Apply(TestDay, Exchange(revision, target: 5), catalog, backend, profile);

        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
        Assert.Equal(0, profile.SaveCount);
    }

    [Fact]
    public void Apply_ExchangeSuccessSwapsCoursesSavesAndReturnsNewRevision()
    {
        var (catalog, backend, profile, plan, subjectIds) = CreateHarness(overlay: true);
        var before = catalog.BuildDay(TestDay);

        var result = ScheduleChangeExecutor.Apply(
            TestDay, Exchange(before.Revision), catalog, backend, profile);

        Assert.True(result.Success);
        Assert.Contains("两节课程已临时交换", result.Message);
        Assert.Equal(subjectIds[1], plan.Classes[0].SubjectId);
        Assert.Equal(subjectIds[0], plan.Classes[1].SubjectId);
        Assert.True(plan.Classes.All(course => course.IsChangedClass));
        Assert.Equal(1, profile.SaveCount);
        Assert.Equal(catalog.BuildDay(TestDay).Revision, result.ScheduleRevision);
        Assert.NotEqual(before.Revision, result.ScheduleRevision);
    }

    [Fact]
    public void Apply_PermanentExchangeWritesSourcePlanWithoutCreatingOverlay()
    {
        var (catalog, backend, profile, plan, subjectIds) = CreateHarness(overlay: false);
        var before = catalog.BuildDay(TestDay);
        var request = Exchange(before.Revision);
        request.Permanent = true;

        var result = ScheduleChangeExecutor.Apply(TestDay, request, catalog, backend, profile);

        Assert.True(result.Success);
        Assert.Contains("永久交换", result.Message);
        Assert.Equal(subjectIds[1], plan.Classes[0].SubjectId);
        Assert.Equal(1, profile.SaveCount);
        Assert.Null(profile.NextOverlayId);
    }

    [Fact]
    public void Apply_SaveFailureRollsBackMutationAndReportsSaveFailed()
    {
        var (catalog, backend, profile, plan, subjectIds) = CreateHarness(overlay: true);
        profile.ThrowOnSave = true;
        var revision = catalog.BuildDay(TestDay).Revision;

        var result = ScheduleChangeExecutor.Apply(TestDay, Exchange(revision), catalog, backend, profile);

        Assert.Equal(CommandResultCodes.SaveFailed, result.Code);
        Assert.Equal(subjectIds[0], plan.Classes[0].SubjectId); // 已回滚。
        Assert.Equal(subjectIds[1], plan.Classes[1].SubjectId);
        Assert.DoesNotContain(plan.Classes, course => course.IsChangedClass);
    }

    [Fact]
    public void GetWritablePlan_OverlayPlanIsReturnedDirectlyWithoutCreatingAnother()
    {
        var (_, backend, profile, plan, _) = CreateHarness(overlay: true);
        profile.NextOverlayId = Guid.NewGuid(); // 不应被使用。

        var writable = ScheduleChangeExecutor.GetWritablePlan(TestDay, false, backend, profile);

        Assert.Same(plan, writable);
    }

    [Fact]
    public void GetWritablePlan_OverlayCreationFailureReturnsNull()
    {
        var (_, backend, profile, _, _) = CreateHarness(overlay: false);
        profile.NextOverlayId = null;

        Assert.Null(ScheduleChangeExecutor.GetWritablePlan(TestDay, false, backend, profile));
    }

    [Fact]
    public void GetWritablePlan_FallsBackToProfileClassPlansWhenBackendStillReturnsSource()
    {
        var (_, backend, profile, _, _) = CreateHarness(overlay: false);
        var overlayId = Guid.NewGuid();
        var overlay = new ClassPlan { Name = "临时课表", IsOverlay = true };
        profile.NextOverlayId = overlayId;
        profile.ClassPlans[overlayId] = overlay;
        // 第二次解析仍返回源课表(非 overlay),但按 overlayId 查 Profile.ClassPlans。
        backend.PlanIdAfterCreation = overlayId;

        var writable = ScheduleChangeExecutor.GetWritablePlan(TestDay, false, backend, profile);

        Assert.Same(overlay, writable);
    }

    [Fact]
    public void GetWritablePlan_PrefersOverlayRefreshedFromBackend()
    {
        var (_, backend, profile, _, _) = CreateHarness(overlay: false);
        var overlayId = Guid.NewGuid();
        var refreshed = new ClassPlan { Name = "临时课表(刷新)", IsOverlay = true };
        profile.NextOverlayId = overlayId;
        backend.PlanAfterCreation = refreshed;
        backend.PlanIdAfterCreation = overlayId;

        var writable = ScheduleChangeExecutor.GetWritablePlan(TestDay, false, backend, profile);

        Assert.Same(refreshed, writable);
    }
}
