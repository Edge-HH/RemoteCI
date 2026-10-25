using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// 老师换课申请：申请/审批/拒绝、跨班互换与冲突回退、替换模式的临时任课老师、
/// 强制换课与撤回后的锁、审批人回退班主任、冲突提示与确认、离线送达、旧插件、班主任撤回与失效，以及 Web Push 载荷加密。
/// 教室端由 FakeAdjustmentPlugin 代替：收到调课集合后按“基础课表 + 调课”改写服务端缓存课表，并像插件一样回执。
/// </summary>
public sealed class ScheduleSwapTests : IDisposable
{
    private const string Password = "Teacher-Password-2026";
    private readonly TestWebApplicationFactory _root = new();
    private readonly FakeAdjustmentPlugin _plugin = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    private readonly string _tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1).ToString("yyyy-MM-dd");

    public ScheduleSwapTests()
    {
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IScheduleAdjustmentChannel>(sp =>
            {
                _plugin.Store = sp.GetRequiredService<IStateStore>();
                _plugin.Scopes = sp.GetRequiredService<IServiceScopeFactory>();
                return _plugin;
            })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task SameClassExchange_RequestApprove_SwapsAndNotifiesBothSides()
    {
        var classId = await SeedClassAsync("一班", ("数学", "王老师"), ("英语", "李老师"));
        var wang = await CreateTeacherAsync("wang", "王老师");
        var li = await CreateTeacherAsync("li", "李老师");
        var wangToken = await TokenAsync("wang");
        var liToken = await TokenAsync("li");

        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(SwapRequestStatus.Pending, created.Status);
        Assert.Equal(["李老师"], created.ApproverNames);
        Assert.Empty(_plugin.Pushes);

        var liNotes = await GetAsync<List<UserNotificationView>>(liToken, "/api/me/notifications");
        Assert.Contains(liNotes, x => x.Kind == UserNotificationKinds.SwapRequested && x.SwapRequestId == created.Id);
        var incoming = await GetAsync<List<SwapRequestView>>(liToken, "/api/swap-requests?box=incoming");
        Assert.True(Assert.Single(incoming).CanDecide);

        // 申请人不能审批自己的申请。
        var self = await SendAsync(wangToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(HttpStatusCode.NotFound, self.StatusCode);

        var approved = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.ShortId}/approve", new SwapDecisionRequest { Note = "好的" });
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        Assert.Equal(SwapDeliveryStates.Applied, approved.Delivery);
        var day = Day(classId, _today);
        Assert.Equal("英语", day.Courses[0].Subject);
        Assert.Equal("数学", day.Courses[1].Subject);
        // 下发的是期望状态：该班两节调课，守卫为各节基础科目。
        var set = _plugin.Pushes.Last(x => x.ClassId == classId);
        Assert.Equal(2, set.Items.Count);
        Assert.All(set.Items, item => Assert.Equal(_today, item.Date));

        var wangNotes = await GetAsync<List<UserNotificationView>>(wangToken, "/api/me/notifications");
        Assert.Contains(wangNotes, x => x.Kind == UserNotificationKinds.SwapApproved);
        Assert.NotEqual(Guid.Empty, wang);
        Assert.NotEqual(Guid.Empty, li);
    }

    [Fact]
    public async Task Create_RequiresOneOwnLesson_AndPermission()
    {
        var classId = await SeedClassAsync("二班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        var token = await TokenAsync("wang");

        var notOwn = await SendAsync(token, HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 1, classId, _today, 2));
        Assert.Equal(HttpStatusCode.BadRequest, notOwn.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapNotOwn, (await notOwn.Content.ReadFromJsonAsync<ApiError>())!.Code);

        await SetTeacherRolePermissionsAsync(remove: UserPermissions.RequestScheduleSwap);
        var denied = await SendAsync(token, HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // 没有强制换课权限时 force=true 被拒绝。
        await SetTeacherRolePermissionsAsync(add: UserPermissions.RequestScheduleSwap);
        var request = Exchange(classId, _today, 0, classId, _today, 1);
        request.Force = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/api/swap-requests", request)).StatusCode);
    }

    [Fact]
    public async Task ReplaceMode_InClassNotTaught_OverridesTeacherIntoPersonalSchedule()
    {
        await SeedClassAsync("三班", ("数学", "王老师"));
        // 四班的数学由张老师教，王老师替换四班第 1 节为自己的数学。
        var other = await SeedClassAsync("四班", ("英语", "李老师"), ("数学", "张老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var wangToken = await TokenAsync("wang");

        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", new CreateSwapRequest
        {
            Mode = SwapMode.Replace,
            Target = new SwapSlot { ClassId = other, Date = _today, Index = 0 },
            SubjectName = "数学",
            Reason = "代课",
            // 种子课表各班第 1 节都在 08:00，王老师在三班也有课：确认任课冲突后继续（例如合班）。
            AcknowledgeConflicts = true,
        });
        Assert.Equal(["李老师"], created.ApproverNames);
        Assert.Contains(created.AcknowledgedConflicts, x => x.Kind == SwapConflictKinds.TeacherClash);
        await PostAsync<SwapRequestView>(await TokenAsync("li"), $"/api/swap-requests/{created.Id}/approve",
            new SwapDecisionRequest { AcknowledgeConflicts = true });

        var course = Day(other, _today).Courses[0];
        Assert.Equal("数学", course.Subject);
        Assert.Equal("王老师", course.Teacher);
        var schedule = await GetAsync<MyScheduleResponse>(wangToken, "/api/me/schedule");
        Assert.Contains(schedule.Days.SelectMany(x => x.Items), x => x.ClassId == other && x.Courses.Any(c => c.Index == 0));
    }

    [Fact]
    public async Task CrossClassExchange_ConflictInOneClass_RevertsBothSidesAndStaysPending()
    {
        var first = await SeedClassAsync("五班", ("数学", "王老师"), ("英语", "孙老师"));
        var second = await SeedClassAsync("六班", ("英语", "李老师"), ("数学", "王老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        // 种子课表各班第 1 节同为 08:00，跨班互换会让两位老师当天与另一个班撞课，这里确认后继续。
        var request = Exchange(first, _today, 0, second, _tomorrow, 0);
        request.AcknowledgeConflicts = true;
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", request);
        var approve = new SwapDecisionRequest { AcknowledgeConflicts = true };

        _plugin.ConflictClass = second;
        var liToken = await TokenAsync("li");
        var failed = await SendAsync(liToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", approve);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapSlotChanged, (await failed.Content.ReadFromJsonAsync<ApiError>())!.Code);
        // 五班那一半也被撤掉，不留半个换课。
        Assert.Equal("数学", Day(first, _today).Courses[0].Subject);
        Assert.Empty(_plugin.Pushes.Last(x => x.ClassId == first).Items);
        Assert.Equal(SwapRequestStatus.Pending, (await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}")).Status);

        _plugin.ConflictClass = null;
        var approved = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}/approve", approve);
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        Assert.Equal("英语", Day(first, _today).Courses[0].Subject);
        Assert.Equal("李老师", Day(first, _today).Courses[0].Teacher);
        Assert.Equal("数学", Day(second, _tomorrow).Courses[0].Subject);
        Assert.Equal("王老师", Day(second, _tomorrow).Courses[0].Teacher);
    }

    [Fact]
    public async Task ForcedSwap_RevokeRestoresAndLocksOnlyThatRequester()
    {
        var classId = await SeedClassAsync("七班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        await CreateTeacherAsync("zhao", "赵老师");
        await SetTeacherRolePermissionsAsync(add: UserPermissions.ForceScheduleSwap);
        var wangToken = await TokenAsync("wang");
        var liToken = await TokenAsync("li");

        var forcedRequest = Exchange(classId, _today, 0, classId, _today, 1);
        forcedRequest.Force = true;
        var forced = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", forcedRequest);
        Assert.Equal(SwapRequestStatus.Forced, forced.Status);
        Assert.Equal("英语", Day(classId, _today).Courses[0].Subject);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(liToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapForced);

        var revoked = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{forced.Id}/revoke", new { });
        Assert.Equal(SwapRequestStatus.Revoked, revoked.Status);
        Assert.Equal("数学", Day(classId, _today).Courses[0].Subject);
        Assert.Equal("英语", Day(classId, _today).Courses[1].Subject);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(wangToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapRevoked);

        var again = await SendAsync(wangToken, HttpMethod.Post, "/api/swap-requests", forcedRequest);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapForceLocked, (await again.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // 普通申请不受锁影响；其他老师对同一节课的强制换课也不受影响（与待审批申请冲突，需要确认）。
        var normal = Exchange(classId, _today, 0, classId, _today, 1);
        Assert.Equal(SwapRequestStatus.Pending, (await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", normal)).Status);
        var zhaoForce = Exchange(classId, _today, 2, classId, _today, 1);
        zhaoForce.Force = true;
        zhaoForce.AcknowledgeConflicts = true;
        Assert.Equal(SwapRequestStatus.Forced, (await PostAsync<SwapRequestView>(await TokenAsync("zhao"), "/api/swap-requests", zhaoForce)).Status);
    }

    [Fact]
    public async Task ForcedSwap_CountsAsPendingUntilSeen_ThenStaysRevocable()
    {
        var classId = await SeedClassAsync("九班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "李老师"));
        await CreateTeacherAsync("wang", "王老师");
        var liId = await CreateTeacherAsync("li", "李老师");
        await SetTeacherRolePermissionsAsync(add: UserPermissions.ForceScheduleSwap);
        var wangToken = await TokenAsync("wang");
        var liToken = await TokenAsync("li");

        var forcedRequest = Exchange(classId, _today, 0, classId, _today, 1);
        forcedRequest.Force = true;
        var first = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", forcedRequest);
        var secondRequest = Exchange(classId, _tomorrow, 0, classId, _tomorrow, 2);
        secondRequest.Force = true;
        var second = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", secondRequest);

        var incoming = await GetAsync<List<SwapRequestView>>(liToken, "/api/swap-requests?box=incoming");
        Assert.All(incoming, x => Assert.True(x.CanRevoke && x.NeedsAction));
        Assert.Equal(2, await PendingCountAsync());

        // 查看详情：本次仍是“新”，之后不再计入待处理，但仍可撤回。
        Assert.True((await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{first.Id}")).NeedsAction);
        var seen = await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{first.Id}");
        Assert.True(seen.CanRevoke);
        Assert.False(seen.NeedsAction);
        Assert.Equal(1, await PendingCountAsync());

        // 打开“待我处理”列表后批量标记。
        var response = await SendAsync(liToken, HttpMethod.Post, "/api/swap-requests/seen", new MarkSwapsSeenRequest { Ids = [second.Id] });
        response.EnsureSuccessStatusCode();
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pending").GetInt32());
        Assert.All(await GetAsync<List<SwapRequestView>>(liToken, "/api/swap-requests?box=incoming"),
            x => Assert.False(x.NeedsAction));
        Assert.DoesNotContain(await GetAsync<List<UserNotificationView>>(liToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapForced && x.ReadAt is null);

        var revoked = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{first.Id}/revoke", new { });
        Assert.Equal(SwapRequestStatus.Revoked, revoked.Status);

        async Task<int> PendingCountAsync()
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ScheduleSwapService>().CountPendingForAsync(liId);
        }
    }

    [Fact]
    public async Task Approver_FallsBackToHomeroomTeacher_WhenCounterpartHasNoAccount()
    {
        var classId = await SeedClassAsync("八班", ("数学", "王老师"), ("音乐", "钱老师"));
        await CreateTeacherAsync("wang", "王老师");
        var homeroomId = await CreateUserAsync("banzhuren", "班主任甲", AccountRole.ClassAdministratorId);
        await AddHomeroomAsync(homeroomId, classId);
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(["班主任甲"], created.ApproverNames);

        var rejected = await PostAsync<SwapRequestView>(await TokenAsync("banzhuren"), $"/api/swap-requests/{created.Id}/reject", new SwapDecisionRequest { Note = "这节课有考试" });
        Assert.Equal(SwapRequestStatus.Rejected, rejected.Status);
        Assert.Empty(_plugin.Pushes);
    }

    [Fact]
    public async Task ReversedDuplicate_FromCounterpart_IsRejected()
    {
        var classId = await SeedClassAsync("重复班", ("数学", "王老师"), ("英语", "李老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));

        // 李老师反过来提交同一对课位的互换：已有相同的待审批申请。
        var reversed = await SendAsync(await TokenAsync("li"), HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 1, classId, _today, 0));
        Assert.Equal(HttpStatusCode.Conflict, reversed.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapStateConflict, (await reversed.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task SharedSlot_NeedsAcknowledgement_AndApprovalInvalidatesTheOtherRequest()
    {
        var classId = await SeedClassAsync("共享班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        await CreateTeacherAsync("zhao", "赵老师");
        var wangRequest = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));

        // 赵老师也想换李老师的第 2 节：先提示同一节课还有待审批申请，确认后才能提交。
        var zhaoToken = await TokenAsync("zhao");
        var unconfirmed = await SendAsync(zhaoToken, HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 2, classId, _today, 1));
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
        var error = (await unconfirmed.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Equal(ApiErrorCodes.SwapConflicts, error.Code);
        Assert.Contains(error.Conflicts!, x => x.Kind == SwapConflictKinds.SlotPending && x.SwapRequestId == wangRequest.Id);
        var preview = await PostAsync<SwapPreviewResponse>(zhaoToken, "/api/swap-requests/preview", Exchange(classId, _today, 2, classId, _today, 1));
        Assert.Contains(preview.Conflicts, x => x.Kind == SwapConflictKinds.SlotPending);
        var confirmed = Exchange(classId, _today, 2, classId, _today, 1);
        confirmed.AcknowledgeConflicts = true;
        var zhaoRequest = await PostAsync<SwapRequestView>(zhaoToken, "/api/swap-requests", confirmed);
        Assert.Contains(zhaoRequest.AcknowledgedConflicts, x => x.Kind == SwapConflictKinds.SlotPending);

        var liToken = await TokenAsync("li");
        var blocked = await SendAsync(liToken, HttpMethod.Post, $"/api/swap-requests/{wangRequest.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{wangRequest.Id}/approve",
            new SwapDecisionRequest { AcknowledgeConflicts = true });

        var invalidated = await GetAsync<SwapRequestView>(zhaoToken, $"/api/swap-requests/{zhaoRequest.Id}");
        Assert.Equal(SwapRequestStatus.Invalidated, invalidated.Status);
        Assert.False(string.IsNullOrEmpty(invalidated.InvalidReason));
        Assert.Contains(await GetAsync<List<UserNotificationView>>(zhaoToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapInvalidated && x.SwapRequestId == zhaoRequest.Id);
    }

    [Fact]
    public async Task TeacherClash_ByRealTime_WarnsRequesterAndApprover()
    {
        // 王老师 08:00 在甲班上数学、09:00 在乙班上数学；李老师想把甲班 09:00 的英语换到 08:00。
        var classA = await SeedClassAsync("甲班", ("数学", "王老师"), ("英语", "李老师"));
        await SeedClassAsync("乙班", ("物理", "赵老师"), ("数学", "王老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var liToken = await TokenAsync("li");

        var request = Exchange(classA, _today, 1, classA, _today, 0);
        var clash = await SendAsync(liToken, HttpMethod.Post, "/api/swap-requests", request);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var conflict = Assert.Single((await clash.Content.ReadFromJsonAsync<ApiError>())!.Conflicts!, x => x.Kind == SwapConflictKinds.TeacherClash);
        Assert.Equal("王老师", conflict.Teacher);
        Assert.Equal("乙班", conflict.ClassName);
        Assert.Equal("09:00", conflict.StartTime);

        request.AcknowledgeConflicts = true;
        var created = await PostAsync<SwapRequestView>(liToken, "/api/swap-requests", request);
        Assert.Equal(SwapRequestStatus.Pending, created.Status);

        // 审批人同样要确认后才能通过（例如合班上课）。
        var wangToken = await TokenAsync("wang");
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(wangToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest())).StatusCode);
        var approved = await PostAsync<SwapRequestView>(wangToken, $"/api/swap-requests/{created.Id}/approve",
            new SwapDecisionRequest { AcknowledgeConflicts = true });
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        Assert.Contains(approved.AcknowledgedConflicts, x => x.Kind == SwapConflictKinds.TeacherClash);
    }

    [Fact]
    public async Task OfflineClassroom_ApprovalIsRecordedAndDeliveredOnReconnect()
    {
        var classId = await SeedClassAsync("离线班", ("数学", "王老师"), ("英语", "李老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));

        _plugin.Offline.Add(classId);
        var liToken = await TokenAsync("li");
        var approved = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        Assert.Equal(SwapDeliveryStates.Pending, approved.Delivery);
        // 服务端按调课记录显示，并标记待生效。
        var pending = Day(classId, _today).Courses[0];
        Assert.Equal("英语", pending.Subject);
        Assert.True(pending.AdjustmentPending);

        _plugin.Offline.Remove(classId);
        using (var scope = _factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>().PushAsync(classId));
        var delivered = await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}");
        Assert.Equal(SwapDeliveryStates.Applied, delivered.Delivery);
        Assert.Null(Day(classId, _today).Courses[0].AdjustmentPending);
    }

    [Fact]
    public async Task OutdatedPlugin_IsAskedToUpgradeBeforeSwapping()
    {
        var classId = await SeedClassAsync("旧插件班", ("数学", "王老师"), ("英语", "李老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));

        _plugin.Outdated.Add(classId);
        var liToken = await TokenAsync("li");
        var response = await SendAsync(liToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ApiErrorCodes.CapabilityUnsupported, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(SwapRequestStatus.Pending, (await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}")).Status);
    }

    [Fact]
    public async Task Homeroom_CanRevertApprovedSwap_EvenAfterTheSlotChanged()
    {
        var classId = await SeedClassAsync("撤回班", ("数学", "王老师"), ("英语", "李老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var homeroomId = await CreateUserAsync("revert.head", "班主任乙", AccountRole.ClassAdministratorId);
        await AddHomeroomAsync(homeroomId, classId);
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        await PostAsync<SwapRequestView>(await TokenAsync("li"), $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal("英语", Day(classId, _today).Courses[0].Subject);

        var headToken = await TokenAsync("revert.head");
        var detail = await GetAsync<SwapRequestView>(headToken, $"/api/swap-requests/{created.Id}");
        Assert.True(detail.CanRevert);
        // 普通老师不能撤回别人已生效的换课。
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(await TokenAsync("li"), HttpMethod.Post, $"/api/swap-requests/{created.Id}/revert", new { })).StatusCode);

        var reverted = await PostAsync<SwapRequestView>(headToken, $"/api/swap-requests/{created.Id}/revert", new { });
        Assert.Equal(SwapRequestStatus.Revoked, reverted.Status);
        Assert.Equal("数学", Day(classId, _today).Courses[0].Subject);
        Assert.Equal("英语", Day(classId, _today).Courses[1].Subject);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(await TokenAsync("wang"), "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapRevoked && x.SwapRequestId == created.Id);
    }

    [Fact]
    public async Task BaseTimetableChange_InvalidatesAppliedSwapAndNotifies()
    {
        var classId = await SeedClassAsync("失效班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var wangToken = await TokenAsync("wang");
        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        await PostAsync<SwapRequestView>(await TokenAsync("li"), $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());

        // 档案下发后，基础课表的第 1 节从数学改成了物理：调课的守卫对不上，整个换课失效。
        _plugin.ChangeBase(classId, _today, 0, "物理");
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>().PushAsync(classId);

        var view = await GetAsync<SwapRequestView>(wangToken, $"/api/swap-requests/{created.Id}");
        Assert.Equal(SwapRequestStatus.Invalidated, view.Status);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(wangToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapInvalidated && x.SwapRequestId == created.Id);
        var day = Day(classId, _today);
        Assert.Equal("物理", day.Courses[0].Subject);
        Assert.Equal("英语", day.Courses[1].Subject);
    }

    [Fact]
    public async Task BuiltinRoles_DefaultToRequestButNotForce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var roleId in new[] { AccountRole.TeacherId, AccountRole.ClassAdministratorId })
        {
            var role = await db.AccountRoles.SingleAsync(x => x.Id == roleId);
            Assert.True(role.DefaultPermissions.HasFlag(UserPermissions.RequestScheduleSwap));
            Assert.False(role.DefaultPermissions.HasFlag(UserPermissions.ForceScheduleSwap));
        }
    }

    [Fact]
    public void WebPush_EncryptsAccordingToRfc8291_AndSignsVapidJwt()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParameters = userAgent.ExportParameters(false);
        var uaPublic = new byte[] { 0x04 }.Concat(uaParameters.Q.X!).Concat(uaParameters.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes("""{"title":"换课"}""");

        var body = WebPushSender.Encrypt(uaPublic, auth, payload);
        var salt = body[..16];
        var keyLength = body[20];
        var serverPublic = body[21..(21 + keyLength)];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var secret = userAgent.DeriveRawSecretAgreement(server.PublicKey);
        var (key, nonce) = WebPushSender.DeriveContentKeys(secret, auth, uaPublic, serverPublic, salt);
        var cipher = body[(21 + keyLength)..^16];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, body[^16..], plain);
        Assert.Equal(0x02, plain[^1]);
        Assert.Equal(payload, plain[..^1]);

        var keys = WebPushSender.GenerateKeys();
        var jwt = WebPushSender.CreateVapidJwt(keys, "https://push.example.com", DateTimeOffset.UtcNow.AddHours(1));
        var parts = jwt.Split('.');
        var publicKey = WebPushSender.Base64UrlDecode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            WebPushSender.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task ActionLink_ShowsConfirmPage_ApprovesOnce_AndRejectsNonApprovers()
    {
        var classId = await SeedClassAsync("链接班", ("数学", "王老师"), ("英语", "李老师"));
        await CreateTeacherAsync("link.wang", "王老师");
        await CreateTeacherAsync("link.li", "李老师");
        var wangToken = await TokenAsync("link.wang");
        var liToken = await TokenAsync("link.li");
        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));

        // 申请人不是审批人，不能为“同意”签发链接。
        var denied = await SendAsync(wangToken, HttpMethod.Post, "/api/me/action-links",
            new ActionLinkRequest { Action = ActionLinkActions.SwapApprove, TargetId = created.Id });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        var link = await PostAsync<ActionLinkResponse>(liToken, "/api/me/action-links",
            new ActionLinkRequest { Action = ActionLinkActions.SwapApprove, TargetId = created.Id });
        Assert.StartsWith("/Act/", link.Path);
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 打开链接只展示确认页（聊天软件预览链接也不会执行）。
        var page = WebUtility.HtmlDecode(await anonymous.GetStringAsync(link.Path));
        Assert.Contains("确认同意换课", page);
        Assert.Equal(SwapRequestStatus.Pending, (await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}")).Status);

        var confirm = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Note"] = "可以" }));
        Assert.Contains("已同意", WebUtility.HtmlDecode(await confirm.Content.ReadAsStringAsync()));
        Assert.Equal(SwapRequestStatus.Approved, (await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}")).Status);

        var again = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Contains("这个链接已经使用过", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));
        Assert.Contains("链接无效或已过期", WebUtility.HtmlDecode(await anonymous.GetStringAsync("/Act/not-a-real-token")));
    }

    // ---------- 辅助 ----------

    private CreateSwapRequest Exchange(Guid sourceClass, string sourceDate, int sourceIndex, Guid targetClass, string targetDate, int targetIndex) => new()
    {
        Mode = SwapMode.Exchange,
        Source = new SwapSlot { ClassId = sourceClass, Date = sourceDate, Index = sourceIndex },
        Target = new SwapSlot { ClassId = targetClass, Date = targetDate, Index = targetIndex },
        Reason = "外出教研",
    };

    /// <summary>新建班级并缓存今明两天相同的课表：第 i 节为 lessons[i] 的学科与老师。</summary>
    private async Task<Guid> SeedClassAsync(string name, params (string Subject, string Teacher)[] lessons)
    {
        var admin = await _root.LoginAsync();
        var created = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/classes", admin.AccessToken, new CreateClassRequest { Name = name }));
        created.EnsureSuccessStatusCode();
        var classId = (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;
        var subjects = lessons.Select(x => new SubjectEntry { Id = Guid.NewGuid(), Name = x.Subject, Teacher = x.Teacher }).ToList();
        ScheduleDay BuildDay(string date) => new()
        {
            Date = date,
            Revision = $"{date}-r0",
            Enabled = true,
            Courses = subjects.Select((subject, index) => new CourseEntry
            {
                Index = index,
                Label = $"第{index + 1}节",
                SubjectId = subject.Id,
                Subject = subject.Name,
                Teacher = subject.Teacher,
                StartTime = $"{8 + index:00}:00",
                EndTime = $"{8 + index:00}:45",
                Enabled = true,
            }).ToList(),
        };
        _factory.Services.GetRequiredService<IStateStore>().SaveSchedule(classId, new ScheduleBundle
        {
            FromDate = _today,
            ClassId = classId,
            Days = [BuildDay(_today), BuildDay(_tomorrow)],
            Subjects = subjects,
        });
        return classId;
    }

    private ScheduleDay Day(Guid classId, string date) =>
        _factory.Services.GetRequiredService<IStateStore>().GetLatestSchedule(classId)!.Days.Single(x => x.Date == date);

    private async Task SetTeacherRolePermissionsAsync(UserPermissions add = UserPermissions.None, UserPermissions remove = UserPermissions.None)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = await db.AccountRoles.SingleAsync(x => x.Id == AccountRole.TeacherId);
        role.DefaultPermissions = (role.DefaultPermissions | add) & ~remove;
        await db.SaveChangesAsync();
    }

    private Task<Guid> CreateTeacherAsync(string username, string displayName) =>
        CreateUserAsync(username, displayName, AccountRole.TeacherId);

    private async Task<Guid> CreateUserAsync(string username, string displayName, Guid roleId)
    {
        var admin = await _root.LoginAsync();
        var response = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest { ClassId = TestWebApplicationFactory.DefaultClassId, Username = $"{username}.swap", DisplayName = displayName, Password = Password, RoleId = roleId }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserListItem>())!.Id;
    }

    private async Task<string> TokenAsync(string username)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = $"{username}.swap",
            Password = Password,
            DeviceName = "Swap Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    private Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, object? body = null) =>
        _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(method, path, token, body));

    private async Task<T> PostAsync<T>(string token, string path, object body)
    {
        var response = await SendAsync(token, HttpMethod.Post, path, body);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{path} 失败（{response.StatusCode}）：{await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(string token, string path)
    {
        var response = await SendAsync(token, HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task AddHomeroomAsync(Guid userId, Guid classId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ClassMemberships.Add(new ClassMembership { UserId = userId, ClassroomId = classId, RoleDefinitionId = AccountRole.ClassAdministratorId });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 假教室端：记住首次下发时的课表作为基础课表；每收到一份调课集合，就按守卫把调课叠加到基础课表上，
    /// 改写服务端缓存课表，并像插件的课表推送那样回执（对账、失效通知）。
    /// </summary>
    private sealed class FakeAdjustmentPlugin : IScheduleAdjustmentChannel
    {
        private readonly ConcurrentDictionary<Guid, ScheduleBundle> _bases = new();
        private int _revision;
        public IStateStore Store { get; set; } = null!;
        public IServiceScopeFactory Scopes { get; set; } = null!;
        /// <summary>该班对收到的每条调课都报告冲突。</summary>
        public Guid? ConflictClass { get; set; }
        public HashSet<Guid> Offline { get; } = [];
        public HashSet<Guid> Outdated { get; } = [];
        public ConcurrentQueue<ScheduleAdjustmentSet> Received { get; } = new();
        public List<ScheduleAdjustmentSet> Pushes => [.. Received];

        public bool? Supports(Guid classId) => Offline.Contains(classId) ? null : !Outdated.Contains(classId);

        /// <summary>模拟档案下发改了基础课表：某天某节改成另一门科目。</summary>
        public void ChangeBase(Guid classId, string date, int index, string subjectName)
        {
            var bundle = Base(classId);
            var subject = bundle.Subjects.Single(x => x.Name == subjectName);
            var course = bundle.Days.Single(x => x.Date == date).Courses.Single(x => x.Index == index);
            course.SubjectId = subject.Id;
            course.Subject = subject.Name;
            course.Teacher = subject.Teacher;
        }

        public async Task<bool> PushAsync(Guid classId, ScheduleAdjustmentSet set, CancellationToken ct)
        {
            Received.Enqueue(set);
            var baseBundle = Base(classId);
            var results = new List<ScheduleAdjustmentItemResult>();
            var days = new List<ScheduleDay>();
            foreach (var day in baseBundle.Days)
            {
                var courses = day.Courses.Select(Clone).ToList();
                foreach (var item in set.Items.Where(x => x.Date == day.Date))
                {
                    var course = courses.SingleOrDefault(x => x.Index == item.Index);
                    if (ConflictClass == classId || course is null || course.SubjectId != item.FromSubjectId)
                    {
                        results.Add(ScheduleAdjustmentMath.Conflict(item.Id, ScheduleAdjustmentConflictCodes.BaseChanged));
                        continue;
                    }
                    var subject = baseBundle.Subjects.Single(x => x.Id == item.ToSubjectId);
                    course.BaseSubjectId = item.FromSubjectId == item.ToSubjectId ? null : item.FromSubjectId;
                    course.SubjectId = subject.Id;
                    course.Subject = subject.Name;
                    course.Teacher = subject.Teacher;
                    course.AdjustmentId = item.Id;
                    results.Add(new ScheduleAdjustmentItemResult { Id = item.Id, State = ScheduleAdjustmentItemState.Applied });
                }
                days.Add(new ScheduleDay
                {
                    Date = day.Date,
                    Revision = $"{day.Date}-r{Interlocked.Increment(ref _revision)}",
                    Enabled = true,
                    Courses = courses,
                });
            }
            var bundle = new ScheduleBundle { FromDate = baseBundle.FromDate, ClassId = classId, Days = days, Subjects = baseBundle.Subjects };
            Store.SaveSchedule(classId, bundle);
            await using var scope = Scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ScheduleAdjustmentService>().ReconcileAsync(classId,
                new ScheduleAdjustmentReport { Version = set.Version, Items = results }, bundle, ct);
            if (outcome.Invalidated.Count > 0)
                await scope.ServiceProvider.GetRequiredService<ScheduleSwapService>().HandleInvalidatedAsync(outcome.Invalidated, ct);
            return true;
        }

        private ScheduleBundle Base(Guid classId) => _bases.GetOrAdd(classId, id =>
        {
            var source = Store.GetSourceSchedule(id)!;
            return new ScheduleBundle
            {
                FromDate = source.FromDate,
                ClassId = id,
                Days = source.Days.Select(day => new ScheduleDay
                {
                    Date = day.Date, Revision = day.Revision, Enabled = day.Enabled, Courses = day.Courses.Select(Clone).ToList(),
                }).ToList(),
                Subjects = source.Subjects,
            };
        });

        private static CourseEntry Clone(CourseEntry x) => new()
        {
            Index = x.Index, Label = x.Label, SubjectId = x.SubjectId, Subject = x.Subject,
            StartTime = x.StartTime, EndTime = x.EndTime, Teacher = x.Teacher, Enabled = x.Enabled,
        };
    }
}
