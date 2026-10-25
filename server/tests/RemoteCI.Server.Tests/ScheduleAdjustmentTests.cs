using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// 调课记录：叠放与撤回回落、守卫继承、对账（送达、冲突整叠失效、收编本地调课）、清扫、升级回填，
/// 以及叠加到插件课表上的显示规则。测试服务没有在线插件，下发与回执由测试直接调用对账模拟。
/// </summary>
public sealed class ScheduleAdjustmentTests : IDisposable
{
    private static readonly Guid ClassId = TestWebApplicationFactory.DefaultClassId;
    private readonly TestWebApplicationFactory _factory = new();
    private readonly Guid _math = Guid.NewGuid();
    private readonly Guid _english = Guid.NewGuid();
    private readonly Guid _physics = Guid.NewGuid();
    private readonly string _today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task StackedAdjustments_InheritGuard_AndRevertFallsBackToLowerOne()
    {
        SeedSchedule();
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>();

        var first = await service.AddAsync([Change(0, _math, "数学", _english, "英语")], ScheduleAdjustmentOrigin.SwapRequest, Guid.NewGuid(), null, "王老师");
        // 第二次换课看到的“当前科目”是英语，但守卫仍应是基础科目数学。
        var second = await service.AddAsync([Change(0, _english, "英语", _physics, "物理")], ScheduleAdjustmentOrigin.SwapRequest, Guid.NewGuid(), null, "赵老师");
        Assert.Equal(_math, second.Rows[0].FromSubjectId);
        Assert.True(second.Versions[ClassId] > first.Versions[ClassId]);

        var set = await service.BuildSetAsync(ClassId);
        var item = Assert.Single(set.Items);
        Assert.Equal((second.Rows[0].Id, _math, _physics), (item.Id, item.FromSubjectId, item.ToSubjectId));

        await service.RevertAsync(second.Rows[0].Id, "撤回");
        item = Assert.Single((await service.BuildSetAsync(ClassId)).Items);
        Assert.Equal(first.Rows[0].Id, item.Id);
        Assert.Equal("英语", Overlaid().Days[0].Courses[0].Subject);
    }

    [Fact]
    public async Task Reconcile_MarksDelivered_InvalidatesWholeStackOnConflict_AndAdoptsLocal()
    {
        SeedSchedule();
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>();
        var swapA = Guid.NewGuid();
        var swapB = Guid.NewGuid();
        var lower = await service.AddAsync([Change(0, _math, "数学", _english, "英语")], ScheduleAdjustmentOrigin.SwapRequest, swapA, null, "王老师");
        var upper = await service.AddAsync([Change(0, _english, "英语", _physics, "物理"), Change(1, _english, "英语", _math, "数学")],
            ScheduleAdjustmentOrigin.SwapRequest, swapB, null, "赵老师");
        var set = await service.BuildSetAsync(ClassId);
        var top0 = set.Items.Single(x => x.Index == 0);
        var top1 = set.Items.Single(x => x.Index == 1);
        var local = new LocalScheduleAdjustment
        {
            Id = Guid.NewGuid(), Date = _today, Index = 2, FromSubjectId = _physics, ToSubjectId = _english,
            Origin = ScheduleAdjustmentOrigins.ClassIsland,
        };

        var outcome = await service.ReconcileAsync(ClassId, new ScheduleAdjustmentReport
        {
            Version = set.Version,
            Items =
            [
                ScheduleAdjustmentMath.Conflict(top0.Id, ScheduleAdjustmentConflictCodes.BaseChanged),
                new ScheduleAdjustmentItemResult { Id = top1.Id, State = ScheduleAdjustmentItemState.Applied },
            ],
            Local = [local],
        }, Bundle());

        // 第 1 节整叠（两次换课各一条）都失效，两次换课都要通知。
        Assert.Equal(new HashSet<Guid> { swapA, swapB }, outcome.Invalidated.Select(x => x.SwapRequestId).ToHashSet());
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ScheduleAdjustments.AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(ScheduleAdjustmentStatus.Invalidated, rows[lower.Rows[0].Id].Status);
        Assert.Equal(ScheduleAdjustmentStatus.Invalidated, rows[upper.Rows[0].Id].Status);
        Assert.Equal(ScheduleAdjustmentConflictCodes.BaseChanged, rows[upper.Rows[0].Id].ConflictCode);
        Assert.Equal(ScheduleAdjustmentDelivery.Applied, rows[upper.Rows[1].Id].Delivery);
        Assert.Equal(ScheduleAdjustmentOrigin.ClassIsland, rows[local.Id].Origin);
        Assert.Equal(("物理", "英语"), (rows[local.Id].FromSubject, rows[local.Id].ToSubject));

        var next = await service.BuildSetAsync(ClassId);
        Assert.True(next.Version > set.Version);
        Assert.Contains(local.Id, next.AcknowledgedLocalIds);
        Assert.Equal([1, 2], next.Items.Select(x => x.Index));

        // 重复上报同一条本地调课不会重复收编。
        await service.ReconcileAsync(ClassId, new ScheduleAdjustmentReport { Version = next.Version, Local = [local] }, Bundle());
        Assert.Equal(1, await db.ScheduleAdjustments.CountAsync(x => x.Id == local.Id));
    }

    [Fact]
    public async Task Sweep_ExpiresPastAdjustments()
    {
        SeedSchedule();
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>();
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1).ToString("yyyy-MM-dd");
        var past = await service.AddAsync([Change(0, _math, "数学", _english, "英语") with { Date = yesterday }],
            ScheduleAdjustmentOrigin.Direct, null, null, "班主任");

        await service.SweepAsync();

        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScheduleAdjustments.AsNoTracking()
            .SingleAsync(x => x.Id == past.Rows[0].Id);
        Assert.Equal(ScheduleAdjustmentStatus.Expired, row.Status);
    }

    [Fact]
    public async Task Backfill_ConvertsLegacyAppliedSwapsOnce()
    {
        SeedSchedule();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requester = await db.Users.Select(x => x.Id).FirstAsync();
        var swapId = Guid.NewGuid();
        db.ScheduleSwapRequests.Add(new ScheduleSwapRequest
        {
            Id = swapId,
            Mode = SwapMode.Replace,
            Status = SwapRequestStatus.Approved,
            RequesterUserId = requester,
            Reason = "旧换课",
            TargetClassId = ClassId,
            TargetDate = _today,
            TargetIndex = 1,
            TargetSubject = "英语",
            ReplacementSubjectName = "物理",
            CounterpartClassId = ClassId,
            CounterpartDate = _today,
            CreatedAt = DateTimeOffset.UtcNow,
            AppliedPlanJson = JsonSerializer.Serialize(new List<ScheduleSwapService.AppliedChange>
            {
                new(ClassId, _today, 1, _english, "英语", "物理", null, null, "代课老师"),
            }),
        });
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>();

        Assert.Equal(1, await service.BackfillAsync());
        Assert.Equal(0, await service.BackfillAsync());

        var row = await db.ScheduleAdjustments.AsNoTracking().SingleAsync(x => x.SwapRequestId == swapId);
        Assert.Equal((ScheduleAdjustmentOrigin.Backfill, _english, _physics, "代课老师"), (row.Origin, row.FromSubjectId, row.ToSubjectId, row.TeacherName));
        Assert.Equal(ScheduleAdjustmentDelivery.Applied, row.Delivery);
    }

    [Fact]
    public void Overlay_ShowsPendingReplacement_TeacherOverride_AndKeepsDayMarkers()
    {
        var overlay = new ScheduleAdjustmentOverlay();
        var applied = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var stale = Guid.NewGuid();
        overlay.ReplaceClass(ClassId,
        [
            // 教室端已是英语：只补上临时任课老师。
            new AdjustmentOverlayEntry(applied, ClassId, _today, 0, _math, _english, "英语", "代课老师"),
            // 教室端仍是原科目（离线未送达）：按记录显示并标记待生效。
            new AdjustmentOverlayEntry(pending, ClassId, _today, 1, _english, _physics, "物理", null),
            // 教室端既不是原科目也不是新科目：以教室端实况为准。
            new AdjustmentOverlayEntry(stale, ClassId, _today, 2, _math, _english, "英语", "某老师"),
        ]);
        var bundle = Bundle(dayKind: ScheduleDayKinds.Makeup, english0: true);

        var result = overlay.Apply(ClassId, bundle);

        var day = Assert.Single(result.Days);
        Assert.Equal((ScheduleDayKinds.Makeup, "国庆节"), (day.DayKind, day.HolidayName));
        Assert.Equal(("英语", "代课老师", applied), (day.Courses[0].Subject, day.Courses[0].Teacher, day.Courses[0].AdjustmentId!.Value));
        Assert.Null(day.Courses[0].AdjustmentPending);
        Assert.Equal(("物理", "赵老师", true), (day.Courses[1].Subject, day.Courses[1].Teacher, day.Courses[1].AdjustmentPending!.Value));
        Assert.Equal(_english, day.Courses[1].BaseSubjectId);
        Assert.Equal("物理", day.Courses[2].Subject);
        Assert.Null(day.Courses[2].AdjustmentId);
    }

    private PlannedSlotChange Change(int index, Guid from, string fromName, Guid to, string toName) =>
        new(ClassId, _today, index, from, fromName, to, toName, null);

    private void SeedSchedule() => _factory.Services.GetRequiredService<IStateStore>().SaveSchedule(ClassId, Bundle());

    private ScheduleBundle Overlaid() => _factory.Services.GetRequiredService<IStateStore>().GetLatestSchedule(ClassId)!;

    /// <summary>今天三节：数学（王）、英语（李）、物理（赵）；english0 时第 1 节已是英语。</summary>
    private ScheduleBundle Bundle(string? dayKind = null, bool english0 = false)
    {
        var subjects = new List<SubjectEntry>
        {
            new() { Id = _math, Name = "数学", Teacher = "王老师" },
            new() { Id = _english, Name = "英语", Teacher = "李老师" },
            new() { Id = _physics, Name = "物理", Teacher = "赵老师" },
        };
        CourseEntry Course(int index, SubjectEntry subject) => new()
        {
            Index = index, Label = $"第{index + 1}节", SubjectId = subject.Id, Subject = subject.Name, Teacher = subject.Teacher,
            StartTime = $"{8 + index:00}:00", EndTime = $"{8 + index:00}:45", Enabled = true,
        };
        return new ScheduleBundle
        {
            FromDate = _today,
            ClassId = ClassId,
            Subjects = subjects,
            Days =
            [
                new ScheduleDay
                {
                    Date = _today,
                    Revision = "r0",
                    Enabled = true,
                    DayKind = dayKind,
                    HolidayName = dayKind is null ? null : "国庆节",
                    Courses = [Course(0, english0 ? subjects[1] : subjects[0]), Course(1, subjects[1]), Course(2, subjects[2])],
                },
            ],
        };
    }
}
