using System.Text.Json;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScheduleOverlayServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 7, 30, 0); // 周四
    private static readonly DateTime Friday = new(2026, 10, 9);
    private static readonly DateTime Makeup = new(2026, 10, 10); // 周六调休上学，跟周三
    private static readonly DateTime Holiday = new(2026, 10, 7);
    private static readonly Guid Math = Guid.NewGuid();
    private static readonly Guid Physics = Guid.NewGuid();
    private static readonly Guid English = Guid.NewGuid();
    private static readonly Guid Chemistry = Guid.NewGuid();

    private sealed class FakeHost : IScheduleOverlayHost
    {
        public Dictionary<Guid, ClassPlan> Plans { get; } = [];
        public Dictionary<DateTime, Guid> Ordered { get; } = [];
        public Dictionary<DayOfWeek, Guid> RulePlans { get; } = [];
        public HashSet<Guid> Subjects { get; } = [Math, Physics, English, Chemistry];
        public Dictionary<Guid, string> LayoutSignatures { get; } = [];
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }

        public IReadOnlyDictionary<Guid, ClassPlan> ClassPlans => Plans;
        public bool SubjectExists(Guid subjectId) => Subjects.Contains(subjectId);
        public Guid? GetOrderedPlanId(DateTime date) => Ordered.TryGetValue(date.Date, out var id) ? id : null;

        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            if (Ordered.TryGetValue(date.Date, out var id) && Plans.TryGetValue(id, out var plan))
            {
                planId = id;
                return plan;
            }
            return ResolveRulePlan(date, out planId);
        }

        public ClassPlan? ResolveRulePlan(DateTime date, out Guid? planId)
        {
            planId = RulePlans.TryGetValue(date.DayOfWeek, out var id) ? id : null;
            return planId is { } found ? Plans[found] : null;
        }

        public string GetLayoutSignature(ClassPlan plan) => LayoutSignatures.GetValueOrDefault(plan.TimeLayoutId, "");

        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date)
        {
            var source = Plans[sourcePlanId];
            var id = Guid.NewGuid();
            var overlay = new ClassPlan { Name = source.Name, IsOverlay = true, OverlaySourceId = sourcePlanId, TimeLayoutId = source.TimeLayoutId };
            foreach (var lesson in source.Classes) overlay.Classes.Add(new ClassInfo { SubjectId = lesson.SubjectId, IsEnabled = true });
            Plans[id] = overlay;
            Ordered[date.Date] = id;
            return id;
        }

        public void SetOrderedPlan(DateTime date, Guid? planId)
        {
            if (planId is { } id) Ordered[date.Date] = id;
            else Ordered.Remove(date.Date);
        }

        public void DeleteOverlayPlan(Guid planId)
        {
            if (Plans.TryGetValue(planId, out var plan) && plan.IsOverlay) Plans.Remove(planId);
        }

        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }

        public Guid AddPlan(string name, bool overlay = false, params Guid[] subjects)
        {
            var id = Guid.NewGuid();
            var plan = new ClassPlan { Name = name, IsOverlay = overlay, TimeLayoutId = Guid.NewGuid() };
            foreach (var subject in subjects) plan.Classes.Add(new ClassInfo { SubjectId = subject, IsEnabled = true });
            Plans[id] = plan;
            return id;
        }

        public List<Guid> SubjectsOn(DateTime date) =>
            Plans[Ordered[date.Date]].Classes.Select(x => x.SubjectId).ToList();
    }

    private sealed class FakeHolidays(HolidayCalendar calendar) : IHolidayCalendarLookup
    {
        public HolidayCalendar Calendar { get; set; } = calendar;
        public HolidayCalendarDay? Find(DateTime date) =>
            Calendar.Enabled ? Calendar.Days.FirstOrDefault(x => x.Date == date.ToString("yyyy-MM-dd")) : null;
    }

    private static FakeHost CreateHost()
    {
        var host = new FakeHost();
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            host.RulePlans[day] = host.AddPlan(day.ToString(), false, Math, Physics);
        // 周三与周五课表不同，便于区分调休跟哪一天。
        host.Plans[host.RulePlans[DayOfWeek.Wednesday]].Classes[1].SubjectId = Chemistry;
        host.Plans[host.RulePlans[DayOfWeek.Friday]].Classes[0].SubjectId = English;
        return host;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"));

    private static ScheduleOverlayService Create(FakeHost host, FakeHolidays? holidays = null, string? statePath = null, string? legacyPath = null) =>
        new(host, new ScheduleOverlayStateFile(statePath ?? Path.Combine(TempDir(), "ScheduleOverlayState.json"), legacyPath),
            holidays ?? new FakeHolidays(new HolidayCalendar { Enabled = false }), NullLogger<ScheduleOverlayService>.Instance);

    private static ScheduleAdjustmentSet Set(long version, params ScheduleSlotChange[] items) => new() { Version = version, Items = [.. items] };

    private static ScheduleSlotChange Item(DateTime date, int index, Guid from, Guid to, Guid? id = null) =>
        new() { Id = id ?? Guid.NewGuid(), Date = date.ToString("yyyy-MM-dd"), Index = index, FromSubjectId = from, ToSubjectId = to };

    [Fact]
    public void ServerItem_CreatesOverlayFromBase_AndRebuildsInPlace()
    {
        var host = CreateHost();
        var service = Create(host);
        var first = Item(Friday, 1, Physics, Chemistry);

        Assert.True(service.ApplyServerSet(Set(1, first), Now));

        var overlayId = host.Ordered[Friday];
        Assert.True(host.Plans[overlayId].IsOverlay);
        Assert.Equal([English, Chemistry], host.SubjectsOn(Friday));
        Assert.True(host.Plans[overlayId].Classes[1].IsChangedClass);
        var report = service.BuildReport();
        Assert.Equal(1, report.Version);
        Assert.Equal(ScheduleAdjustmentItemState.Applied, Assert.Single(report.Items).State);
        Assert.True(service.TryGetDate("2026-10-09", out var baseSubjects, out var applied));
        Assert.Equal([English, Physics], baseSubjects);
        Assert.Equal(first.Id, applied[1]);

        service.ApplyServerSet(Set(2, Item(Friday, 1, Physics, Math)), Now);

        Assert.Equal(overlayId, host.Ordered[Friday]);
        Assert.Equal([English, Math], host.SubjectsOn(Friday));
    }

    [Fact]
    public void OlderSet_IsIgnored_AndEmptySetRemovesOurOverlayAndRestoresAdminPlan()
    {
        var host = CreateHost();
        var admin = host.AddPlan("考试", false, Math, Physics);
        host.Ordered[Friday] = admin;
        var service = Create(host);
        service.ApplyServerSet(Set(3, Item(Friday, 0, Math, English)), Now);
        Assert.NotEqual(admin, host.Ordered[Friday]);
        Assert.Equal([English, Physics], host.SubjectsOn(Friday));

        Assert.False(service.ApplyServerSet(Set(2), Now));
        Assert.Equal([English, Physics], host.SubjectsOn(Friday));

        service.ApplyServerSet(Set(4), Now);

        Assert.Equal(admin, host.Ordered[Friday]);
        Assert.Equal(host.RulePlans.Count + 1, host.Plans.Count);
    }

    [Fact]
    public void GuardMismatch_ReportsBaseChanged_WithoutTouchingThatLesson()
    {
        var host = CreateHost();
        var service = Create(host);
        var stale = Item(Friday, 0, Math, Chemistry);

        service.ApplyServerSet(Set(1, stale, Item(Friday, 1, Physics, Math)), Now);

        Assert.Equal([English, Math], host.SubjectsOn(Friday));
        var conflict = service.BuildReport().Items.Single(x => x.Id == stale.Id);
        Assert.Equal((ScheduleAdjustmentItemState.Conflict, ScheduleAdjustmentConflictCodes.BaseChanged), (conflict.State, conflict.Code));
    }

    [Fact]
    public void MissingOverlay_IsRebuiltOnVerify()
    {
        var host = CreateHost();
        var service = Create(host);
        service.ApplyServerSet(Set(1, Item(Friday, 0, English, Math)), Now);
        // 例如“新建并启用”档案后临时层全部丢失。
        host.Plans.Remove(host.Ordered[Friday]);
        host.Ordered.Remove(Friday);

        Assert.True(service.Verify(Now));

        Assert.Equal([Math, Physics], host.SubjectsOn(Friday));
    }

    [Fact]
    public void ClassIslandEdit_ToManagedOverlay_BecomesLocalAdjustment()
    {
        var host = CreateHost();
        var service = Create(host);
        service.ApplyServerSet(Set(1, Item(Friday, 0, English, Math)), Now);

        host.Plans[host.Ordered[Friday]].Classes[1].SubjectId = Chemistry;
        service.Verify(Now);

        var local = Assert.Single(service.BuildReport().Local);
        Assert.Equal((1, Physics, Chemistry, ScheduleAdjustmentOrigins.ClassIsland), (local.Index, local.FromSubjectId, local.ToSubjectId, local.Origin));
        Assert.Equal([Math, Chemistry], host.SubjectsOn(Friday));

        // 服务端收编后在集合中确认：本地待报清空，节次照旧。
        service.ApplyServerSet(new ScheduleAdjustmentSet
        {
            Version = 2,
            Items = [Item(Friday, 0, English, Math), Item(Friday, 1, Physics, Chemistry, local.Id)],
            AcknowledgedLocalIds = [local.Id],
        }, Now);
        Assert.Empty(service.BuildReport().Local);
        Assert.Equal([Math, Chemistry], host.SubjectsOn(Friday));
    }

    [Fact]
    public void ForeignOverlay_IsAdoptedWhenLessonsLineUp_AndUnmanagedOtherwise()
    {
        var host = CreateHost();
        var service = Create(host);
        service.ApplyServerSet(Set(1), Now);
        // ClassIsland 自带换课建立的临时层：第 1 节改成了化学。
        var foreign = host.AddPlan("临时层", true, Chemistry, Physics);
        host.Ordered[Friday] = foreign;
        // 另一天是节数不同的考试临时层。
        var monday = new DateTime(2026, 10, 12);
        var exam = host.AddPlan("考试", true, Math, Physics, English);
        host.Ordered[monday] = exam;
        var blocked = Item(monday, 0, Math, Chemistry);
        service.ApplyServerSet(Set(2, blocked), Now);

        var local = Assert.Single(service.BuildReport().Local);
        Assert.Equal((0, English, Chemistry), (local.Index, local.FromSubjectId, local.ToSubjectId));
        Assert.Equal(foreign, host.Ordered[Friday]);
        Assert.Equal([Chemistry, Physics], host.SubjectsOn(Friday));

        var report = service.BuildReport();
        Assert.Equal(["2026-10-12"], report.UnmanagedDates);
        Assert.Equal(ScheduleAdjustmentConflictCodes.ForeignOverlay, report.Items.Single(x => x.Id == blocked.Id).Code);
        Assert.Equal([Math, Physics, English], host.SubjectsOn(monday));
    }

    [Fact]
    public void Makeup_UsesFollowWeekdayBase_IgnoringThatDaysOverlay()
    {
        var host = CreateHost();
        var wednesday = host.RulePlans[DayOfWeek.Wednesday];
        // 周三当天有人换过课：调休上学日不能把这次换课复制过来。
        var swapped = host.AddPlan("周三临时层", true, Physics, Math);
        host.Ordered[Holiday] = swapped;
        var holidays = new FakeHolidays(HolidayCalendarStoreTests.National());
        var service = Create(host, holidays);

        Assert.True(service.Recompose(Now));

        var overlay = host.Plans[host.Ordered[Makeup]];
        Assert.Equal(wednesday, overlay.OverlaySourceId);
        Assert.Equal([Math, Chemistry], host.SubjectsOn(Makeup));

        // 调休安排改成跟周五：按新的基础课表重建。
        holidays.Calendar = HolidayCalendarStoreTests.National(followWeekday: 5);
        service.Recompose(Now);
        Assert.Equal(host.RulePlans[DayOfWeek.Friday], host.Plans[host.Ordered[Makeup]].OverlaySourceId);
        Assert.Equal([English, Physics], host.SubjectsOn(Makeup));

        // 取消调休：撤掉插件建立的临时层。
        holidays.Calendar = HolidayCalendarStoreTests.National(followWeekday: null);
        service.Recompose(Now);
        Assert.False(host.Ordered.ContainsKey(Makeup));
    }

    [Fact]
    public void SwapOnMakeupDay_SurvivesMakeupRebuild()
    {
        var host = CreateHost();
        var holidays = new FakeHolidays(HolidayCalendarStoreTests.National());
        var service = Create(host, holidays);
        service.ApplyServerSet(Set(1, Item(Makeup, 1, Chemistry, English)), Now);
        Assert.Equal([Math, English], host.SubjectsOn(Makeup));

        // 档案下发改了周三课表的时间表：临时层按新时间表重建，换课仍在。
        host.LayoutSignatures[host.Plans[host.RulePlans[DayOfWeek.Wednesday]].TimeLayoutId] = "changed";
        var before = host.Ordered[Makeup];
        service.Recompose(Now);

        Assert.NotEqual(before, host.Ordered[Makeup]);
        Assert.Equal([Math, English], host.SubjectsOn(Makeup));
    }

    [Fact]
    public void HolidayOffDay_ReportsHolidayConflict()
    {
        var host = CreateHost();
        var service = Create(host, new FakeHolidays(HolidayCalendarStoreTests.National()));
        var item = Item(Holiday, 0, Math, English);

        service.ApplyServerSet(Set(1, item), new DateTime(2026, 10, 6, 8, 0, 0));

        Assert.Equal(ScheduleAdjustmentConflictCodes.Holiday, Assert.Single(service.BuildReport().Items).Code);
        Assert.False(host.Ordered.ContainsKey(Holiday));
    }

    [Fact]
    public void LocalChange_IsRecordedWithRequester_AndAppliedThroughOverlay()
    {
        var host = CreateHost();
        var service = Create(host);
        var teacher = new UserProfile { Id = Guid.NewGuid(), Username = "head", DisplayName = "班主任" };

        var result = service.TryApplyLocalChange(Friday, new ScheduleChangeRequest
        {
            Date = "2026-10-09", Mode = ScheduleChangeMode.Exchange, SourceIndex = 0, TargetIndex = 1, ExpectedRevision = "r",
        }, teacher, Now);

        Assert.True(result!.Success);
        Assert.Equal([Physics, English], host.SubjectsOn(Friday));
        var local = service.BuildReport().Local;
        Assert.Equal(2, local.Count);
        Assert.All(local, x => Assert.Equal((ScheduleAdjustmentOrigins.Direct, teacher.Id, "班主任"), (x.Origin, x.RequestedByUserId!.Value, x.RequestedByName!)));
        Assert.Contains(local, x => (x.Index, x.FromSubjectId, x.ToSubjectId) == (0, English, Physics));
    }

    [Fact]
    public void LocalChange_SaveFailure_IsUndone()
    {
        var host = CreateHost();
        var service = Create(host);
        host.ThrowOnSave = true;

        var result = service.TryApplyLocalChange(Friday, new ScheduleChangeRequest
        {
            Date = "2026-10-09", Mode = ScheduleChangeMode.Replace, SourceIndex = 0, ReplacementSubjectId = Math, ExpectedRevision = "r",
        }, null, Now);

        Assert.Equal(CommandResultCodes.SaveFailed, result!.Code);
        Assert.Empty(service.BuildReport().Local);
        Assert.Equal(English, host.Plans[host.Ordered[Friday]].Classes[0].SubjectId);
    }

    [Fact]
    public void LegacyHolidayState_IsImported_KeepingSwapsInsideMakeupOverlay()
    {
        var host = CreateHost();
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        // 旧版调休临时层（跟周三），其中第 1 节被旧版换课改成了英语。
        var legacyOverlay = host.AddPlan("周三", true, English, Chemistry);
        host.Plans[legacyOverlay].OverlaySourceId = host.RulePlans[DayOfWeek.Wednesday];
        host.Ordered[Makeup] = legacyOverlay;
        var legacyPath = Path.Combine(dir, "HolidayState.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            makeups = new Dictionary<string, object> { ["2026-10-10"] = new { planId = legacyOverlay, followWeekday = 3 } },
        }, JsonDefaults.Options));
        var service = Create(host, new FakeHolidays(HolidayCalendarStoreTests.National()), Path.Combine(dir, "ScheduleOverlayState.json"), legacyPath);

        service.Recompose(Now);

        Assert.Equal(legacyOverlay, host.Ordered[Makeup]);
        Assert.Equal([English, Chemistry], host.SubjectsOn(Makeup));
        var local = Assert.Single(service.BuildReport().Local);
        Assert.Equal((0, Math, English), (local.Index, local.FromSubjectId, local.ToSubjectId));
    }

    [Fact]
    public void AdjustmentsMessage_IsReadFromEnvelope_AndReportFeedsScheduleSignature()
    {
        var item = Item(Friday, 0, English, Math);
        var envelope = JsonSerializer.Deserialize<Envelope>(
            JsonSerializer.Serialize(Envelope.ScheduleAdjustments(Set(7, item)), JsonDefaults.Options), JsonDefaults.Options)!;

        Assert.True(ScheduleAdjustmentsMessage.TryRead(envelope, out var set));
        Assert.Equal((7, item.Id), (set.Version, Assert.Single(set.Items).Id));
        Assert.False(ScheduleAdjustmentsMessage.TryRead(Envelope.SchedulePull(), out _));

        // 回执变化（版本、结果、本地调课）必须改变课表签名，否则不会触发重新上报。
        var host = CreateHost();
        var service = Create(host);
        var catalog = new ScheduleCatalog(new FakeBackend(host), overlays: service);
        var before = StateCollector.BuildScheduleSignature(catalog.BuildBundle(Now));
        service.ApplyServerSet(set, Now);
        var bundle = catalog.BuildBundle(Now);
        Assert.NotEqual(before, StateCollector.BuildScheduleSignature(bundle));
        Assert.Equal(7, bundle.Adjustments!.Version);
        var friday = bundle.Days.Single(x => x.Date == "2026-10-09").Courses[0];
        Assert.Equal((Math, English, item.Id), (friday.SubjectId, friday.BaseSubjectId!.Value, friday.AdjustmentId!.Value));
    }

    private sealed class FakeBackend(FakeHost host) : IScheduleBackend
    {
        public IReadOnlyDictionary<Guid, Subject> Subjects { get; } =
            new[] { Math, Physics, English, Chemistry }.ToDictionary(x => x, x => new Subject { Name = x.ToString("N")[..6] });
        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => host.GetClassPlan(date, out planId);
    }

    [Fact]
    public void State_SurvivesRestart()
    {
        var host = CreateHost();
        var path = Path.Combine(TempDir(), "ScheduleOverlayState.json");
        Create(host, statePath: path).ApplyServerSet(Set(5, Item(Friday, 0, English, Math)), Now);
        var overlayId = host.Ordered[Friday];

        var restarted = Create(host, statePath: path);
        restarted.Recompose(Now);

        Assert.Equal(5, restarted.BuildReport().Version);
        Assert.Equal(overlayId, host.Ordered[Friday]);
        Assert.Equal([Math, Physics], host.SubjectsOn(Friday));
    }
}
