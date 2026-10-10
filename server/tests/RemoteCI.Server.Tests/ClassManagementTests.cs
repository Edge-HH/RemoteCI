using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Pages;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>多班级管理：班级 CRUD、批量操作、成员与按班级的权限隔离。</summary>
public sealed class ClassManagementTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ClassManagementTests(TestWebApplicationFactory factory) => _factory = factory;

    // ---------- StateStore 按班级分桶（纯内存单元测试） ----------

    [Fact]
    public void StateStore_BucketsAreIsolatedPerClass()
    {
        IStateStore store = new StateStore();
        var classA = TestWebApplicationFactory.DefaultClassId;
        var classB = Guid.NewGuid();
        var snapshotA = new ClassStateSnapshot { CurrentSubject = "A班数学" };
        var snapshotB = new ClassStateSnapshot { CurrentSubject = "B班语文" };
        store.SaveSnapshot(classA, snapshotA);
        store.SaveSnapshot(classB, snapshotB);

        Assert.Equal("A班数学", store.GetLatestSnapshot(classA)!.CurrentSubject);
        Assert.Equal("B班语文", store.GetLatestSnapshot(classB)!.CurrentSubject);

        store.SaveSchedule(classB, new ScheduleBundle { FromDate = "2026-09-26" });
        Assert.Null(store.GetLatestSchedule(classA));
        Assert.NotNull(store.GetLatestSchedule(classB));

        store.SaveEvent(classB, new ClassEvent { Event = ClassEventKind.OnClass });
        Assert.Null(store.GetLatestEvent(classA));
        store.SaveExtensions(classA, [new ExtensionDefinition { Id = "ext-a" }]);
        Assert.Null(store.GetLatestExtensions(classB));
        Assert.Single(store.GetLatestExtensions(classA)!);
    }

    // ---------- 班级 CRUD 与权限 ----------

    [Fact]
    public async Task ClassCrud_OnlyAdminCanManage()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();

        // 普通用户不能管理班级。
        await CreateUserAsync("plain.user", "Plain-User-Password-2026");
        var user = await _factory.LoginAsync("plain.user", "Plain-User-Password-2026");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", user.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", user.AccessToken,
                new CreateClassRequest { Name = "越权班级" }))).StatusCode);

        // 管理员创建、重命名、重复名拒绝。
        var created = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
            new CreateClassRequest { Name = "高二（1）班" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = (await created.Content.ReadFromJsonAsync<ClassDetail>())!;
        Assert.Equal("高二（1）班", detail.Name);
        Assert.Equal(0, detail.MemberCount);

        var duplicate = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
            new CreateClassRequest { Name = "高二（1）班" }));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var renamed = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{detail.Id}", admin.AccessToken,
            new UpdateClassRequest { Name = "高二（2）班" }));
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        // 空名拒绝；不存在的班级删除返回 404（不再有受保护的默认班级）。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
                new CreateClassRequest { Name = " " }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/classes/{Guid.NewGuid()}", admin.AccessToken))).StatusCode);

        // 每班访客开关。
        var toggle = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{detail.Id}/visitor", admin.AccessToken,
            new ClassVisitorRequest { Enabled = true }));
        Assert.Equal(HttpStatusCode.NoContent, toggle.StatusCode);
        var list = (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken))).Content;
        var classes = (await list.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.Contains(classes, x => x.Id == detail.Id && x.VisitorEnabled);

        var deleted = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/classes/{detail.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    // ---------- 批量操作 ----------

    [Fact]
    public async Task BatchOperations_ToggleVisitorAndDeleteReportPerClassResults()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classA = (await CreateClassAsync(admin.AccessToken, "批量一班"))!;
        var classB = (await CreateClassAsync(admin.AccessToken, "批量二班"))!;

        var enable = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest
            {
                ClassIds = [classA.Id, classB.Id, TestWebApplicationFactory.DefaultClassId],
                Operation = "enableVisitor",
            }));
        enable.EnsureSuccessStatusCode();
        var enableResult = (await enable.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.All(enableResult.Results, x => Assert.True(x.Success));
        var listed = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        // 只断言本测试创建的班级与默认班级；类夹具中其他测试创建的班级不参与断言。
        Assert.All(new[] { classA.Id, classB.Id, TestWebApplicationFactory.DefaultClassId },
            id => Assert.True(listed.Single(x => x.Id == id).VisitorEnabled));

        var disable = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest
            {
                ClassIds = [classA.Id, classB.Id],
                Operation = "disableVisitor",
            }));
        disable.EnsureSuccessStatusCode();
        var afterDisable = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.All(new[] { classA.Id, classB.Id },
            id => Assert.False(afterDisable.Single(x => x.Id == id).VisitorEnabled));

        // 不存在的班级删除失败但不影响其他班级；未知操作被拒绝。
        var missing = Guid.NewGuid();
        var batchDelete = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest
            {
                ClassIds = [classA.Id, missing],
                Operation = "delete",
            }));
        batchDelete.EnsureSuccessStatusCode();
        var deleteResult = (await batchDelete.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.Equal(2, deleteResult.Results.Count);
        Assert.Contains(deleteResult.Results, x => x.ClassId == classA.Id && x.Success);
        Assert.Contains(deleteResult.Results, x => x.ClassId == missing && !x.Success);
        var afterDelete = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.DoesNotContain(afterDelete, x => x.Id == classA.Id);
        Assert.Contains(afterDelete, x => x.Id == TestWebApplicationFactory.DefaultClassId);
    }

    // ---------- 成员管理与按班级权限隔离 ----------

    [Fact]
    public async Task Members_ClassScopedAccess_IsEnforcedPerClass()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classB = (await CreateClassAsync(admin.AccessToken, "隔离一班"))!;

        var userId = await CreateUserAsync("iso.student", "Iso-Student-Password-2026");

        // 测试账号创建时加入了夹具班级；再把 TA 加进 B 班。
        var update = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.StudentId }] }));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var members = (await client.SendAsync(Bearer(HttpMethod.Get, $"/api/classes/{classB.Id}/members", admin.AccessToken))).Content;
        var memberList = (await members.ReadFromJsonAsync<List<ClassMemberInfo>>())!;
        Assert.Single(memberList, x => x.UserId == userId);

        var student = await _factory.LoginAsync("iso.student", "Iso-Student-Password-2026");
        var myClasses = (await client.SendAsync(Bearer(HttpMethod.Get, "/api/me/classes", student.AccessToken))).Content;
        var summaries = (await myClasses.ReadFromJsonAsync<List<ClassSummary>>())!;
        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, x => x.Id == TestWebApplicationFactory.DefaultClassId);
        Assert.Contains(summaries, x => x.Id == classB.Id && x.RoleName == "学生");

        // 是 B 班成员：可以访问（无数据 404）；不是 C 班成员：403。
        var classC = (await CreateClassAsync(admin.AccessToken, "隔离二班"))!;
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Bearer(HttpMethod.Get, $"/api/state?classId={classB.Id}", student.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Get, $"/api/state?classId={classC.Id}", student.AccessToken))).StatusCode);

        // 成员关系移除后，B 班也变为 403。
        var clear = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [] }));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Get, $"/api/state?classId={classB.Id}", student.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task ClassAdmin_CanCommandOwnClassButNotOthers()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classB = (await CreateClassAsync(admin.AccessToken, "班管一班"))!;
        var userId = await CreateUserAsync("cls.admin", "Cls-Admin-Password-2026", AccountRole.ClassAdministratorId);

        // 班主任创建时加入了夹具班级；同时成为 B 班班主任。
        var put = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.ClassAdministratorId }] }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var classAdmin = await _factory.LoginAsync("cls.admin", "Cls-Admin-Password-2026");
        var myClasses = (await client.SendAsync(Bearer(HttpMethod.Get, "/api/me/classes", classAdmin.AccessToken))).Content;
        var summaries = (await myClasses.ReadFromJsonAsync<List<ClassSummary>>())!;
        Assert.Contains(summaries, x => x.Id == classB.Id && x.Permissions!.Value.HasFlag(UserPermissions.SendNotifications));

        // 未加入的班级：命令在权限检查阶段就被拒绝（403），不进入路由。
        var forbidden = await client.SendAsync(Bearer(
            HttpMethod.Post, $"/api/commands?classId={(await CreateClassAsync(admin.AccessToken, "班管二班"))!.Id}", classAdmin.AccessToken,
            new CommandMessage { Command = CommandKind.SendNotification, Notification = new NotificationRequest() }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ---------- WebSocket 按班级扇出 ----------

    [Fact]
    public async Task WebSocket_PluginStateForClassDoesNotReachNonMembers()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var userId = await CreateUserAsync("b.only", "B-Only-Password-2026");
        var classB = (await CreateClassAsync(admin.AccessToken, "推送一班"))!;

        // 先把 B 班成员设为该用户，再把夹具班级成员清空，使该用户只属于 B 班。
        var putB = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.StudentId }] }));
        Assert.Equal(HttpStatusCode.NoContent, putB.StatusCode);
        var clearDefault = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{TestWebApplicationFactory.DefaultClassId}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [] }));
        Assert.Equal(HttpStatusCode.NoContent, clearDefault.StatusCode);

        // 默认班级插件推送状态；非成员的手表不应收到，管理员手表应收到。
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        using var adminWatch = await ConnectWatchAsync();
        using var memberWatch = await ConnectWatchAsync("b.only", "B-Only-Password-2026");

        await SendAsync(plugin, Envelope.StatePush(new ClassStateSnapshot { CurrentSubject = "默认班级专属" }));

        var adminPush = await ReceivePayloadAsync<ClassStateSnapshot>(adminWatch, Protocol.MessageTypeStatePush);
        Assert.Equal("默认班级专属", adminPush.CurrentSubject);

        // B 班成员连接初始化时没有 B 班数据，之后也不应收到默认班级的推送。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ReceiveEnvelopeAsync(memberWatch, Protocol.MessageTypeStatePush));
    }

    // ---------- 班级分组 ----------

    [Fact]
    public async Task Groups_AdminCanManageAndAssignClasses()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();

        var group = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "高一年级" }))).Content.ReadFromJsonAsync<ClassGroupInfo>())!;
        var duplicate = await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "高一年级" }));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var classA = (await CreateClassAsync(admin.AccessToken, "分组一班"))!;
        var classB = (await CreateClassAsync(admin.AccessToken, "分组二班"))!;
        foreach (var classroom in new[] { classA, classB })
        {
            var assign = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classroom.Id}/groups", admin.AccessToken,
                new UpdateClassGroupsRequest { GroupIds = [group.Id] }));
            Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        }

        var listed = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/class-groups", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassGroupInfo>>())!;
        Assert.Contains(listed, x => x.Id == group.Id && x.ClassCount == 2 && x.Depth == 1);

        var details = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.All(details.Where(x => x.Id == classA.Id || x.Id == classB.Id), x => Assert.Equal(["高一年级"], x.GroupNames));

        var rename = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/class-groups/{group.Id}", admin.AccessToken,
            new UpdateClassGroupRequest { Name = "高一（2026级）" }));
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        // 删除分组后班级回到未分组，班级本身保留。
        var deleted = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/class-groups/{group.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        details = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.All(details.Where(x => x.Id == classA.Id || x.Id == classB.Id), x => Assert.Empty(x.GroupNames ?? []));
    }

    [Fact]
    public async Task BatchOperations_CanTargetGroups()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var group = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "广播一年级" }))).Content.ReadFromJsonAsync<ClassGroupInfo>())!;
        var classA = (await CreateClassAsync(admin.AccessToken, "组内一班"))!;
        var classB = (await CreateClassAsync(admin.AccessToken, "组内二班"))!;
        foreach (var classroom in new[] { classA, classB })
            await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classroom.Id}/groups", admin.AccessToken,
                new UpdateClassGroupsRequest { GroupIds = [group.Id] }));

        // 按分组批量开启访客功能。
        var batch = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest { GroupIds = [group.Id], Operation = "enableVisitor" }));
        batch.EnsureSuccessStatusCode();
        var result = (await batch.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.Equal(2, result.Results.Count(x => x.Success));
        var details = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.All(details.Where(x => x.Id == classA.Id || x.Id == classB.Id), x => Assert.True(x.VisitorEnabled));

        // 不存在的分组直接报 404，而不是静默忽略。
        var missing = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest { GroupIds = [Guid.NewGuid()], Operation = "enableVisitor" }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---------- 批量通知广播 ----------

    [Fact]
    public async Task BroadcastNotification_RoutesPerClassAndReportsResults()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);

        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classB = (await CreateClassAsync(admin.AccessToken, "广播二班"))!;

        // 向“默认班级（插件在线）+ 二班（无插件）”广播：逐班独立回报。
        var broadcastTask = client.SendAsync(Bearer(HttpMethod.Post, "/api/commands/broadcast", admin.AccessToken,
            new BroadcastCommandRequest
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest { Title = "全体注意", Message = "广播测试" },
                ClassIds = [TestWebApplicationFactory.DefaultClassId, classB.Id],
            }));

        // 默认班级插件收到命令后立即回执成功。
        var command = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = command.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok },
        });
        var response = await broadcastTask;
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;

        Assert.Equal(2, result.Results.Count);
        var defaultResult = result.Results.Single(x => x.ClassId == TestWebApplicationFactory.DefaultClassId);
        var classBResult = result.Results.Single(x => x.ClassId == classB.Id);
        Assert.True(defaultResult.Success);
        Assert.False(classBResult.Success);
        Assert.Contains("插件未在线", classBResult.Message);
    }

    [Fact]
    public async Task BroadcastNotification_ChecksPermissionPerClass()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        await CreateUserAsync("bcast.student", "Bcast-Student-Password-2026");

        // 完全没有通知权限的学生：所有目标班级都因权限被拒，而不是泄漏插件状态。
        var student = await _factory.LoginAsync("bcast.student", "Bcast-Student-Password-2026");
        var denied = await client.SendAsync(Bearer(HttpMethod.Post, "/api/commands/broadcast", student.AccessToken,
            new BroadcastCommandRequest
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest { Title = "越权广播" },
                ClassIds = [TestWebApplicationFactory.DefaultClassId],
            }));
        denied.EnsureSuccessStatusCode();
        var result = (await denied.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.False(result.Results.Single().Success);
        Assert.Contains("没有该班级的操作权限", result.Results.Single().Message);

        // 班主任（班内含通知权限）可以广播自己管理的班级；权限通过后失败原因是插件离线。
        var classB = (await CreateClassAsync(admin.AccessToken, "广播三班"))!;
        var adminUser = await CreateUserAsync("bcast.admin", "Bcast-Admin-Password-2026", AccountRole.ClassAdministratorId);
        await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = adminUser, RoleId = AccountRole.ClassAdministratorId }] }));
        var classAdmin = await _factory.LoginAsync("bcast.admin", "Bcast-Admin-Password-2026");
        var allowed = await client.SendAsync(Bearer(HttpMethod.Post, "/api/commands/broadcast", classAdmin.AccessToken,
            new BroadcastCommandRequest
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest { Title = "班内广播" },
                ClassIds = [classB.Id],
            }));
        allowed.EnsureSuccessStatusCode();
        var allowedResult = (await allowed.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.False(allowedResult.Results.Single().Success);
        Assert.DoesNotContain("没有该班级的操作权限", allowedResult.Results.Single().Message);
        Assert.Contains("插件未在线", allowedResult.Results.Single().Message);
    }

    [Fact]
    public async Task BatchGroupAssignment_SupportsAddRemoveReplaceAndClear()
    {
        using var scope = _factory.Services.CreateScope();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var firstGroup = await classrooms.CreateGroupAsync($"批量分组甲-{suffix}", null);
        var secondGroup = await classrooms.CreateGroupAsync($"批量分组乙-{suffix}", null);
        var firstClass = await classrooms.CreateAsync($"批量分组一班-{suffix}");
        var secondClass = await classrooms.CreateAsync($"批量分组二班-{suffix}");

        await classrooms.SetClassGroupsAsync(firstClass.Id, [firstGroup.Id]);
        await classrooms.SetClassGroupsAsync(secondClass.Id, [secondGroup.Id]);

        var added = await classrooms.BatchSetClassGroupsAsync(
            [firstClass.Id, secondClass.Id], [firstGroup.Id], "addGroups");
        Assert.All(added.Results, x => Assert.True(x.Success));

        var details = await classrooms.ListAsync();
        Assert.Contains(firstGroup.Id, details.Single(x => x.Id == firstClass.Id).GroupIds ?? []);
        Assert.Contains(firstGroup.Id, details.Single(x => x.Id == secondClass.Id).GroupIds ?? []);
        Assert.Contains(secondGroup.Id, details.Single(x => x.Id == secondClass.Id).GroupIds ?? []);

        var removed = await classrooms.BatchSetClassGroupsAsync(
            [secondClass.Id], [secondGroup.Id], "removeGroups");
        Assert.True(removed.Results.Single().Success);
        details = await classrooms.ListAsync();
        Assert.DoesNotContain(secondGroup.Id, details.Single(x => x.Id == secondClass.Id).GroupIds ?? []);

        var replaced = await classrooms.BatchSetClassGroupsAsync(
            [firstClass.Id], [secondGroup.Id], "replaceGroups");
        Assert.True(replaced.Results.Single().Success);
        details = await classrooms.ListAsync();
        Assert.Equal(new[] { secondGroup.Id }, details.Single(x => x.Id == firstClass.Id).GroupIds ?? []);

        var cleared = await classrooms.BatchSetClassGroupsAsync(
            [firstClass.Id], [], "clearGroups");
        Assert.True(cleared.Results.Single().Success);
        details = await classrooms.ListAsync();
        Assert.Empty(details.Single(x => x.Id == firstClass.Id).GroupIds ?? []);
    }

    [Fact]
    public async Task ClassesPage_RendersTreeAndBatchControlsForSelectedGroup()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Guid childId;
        using (var scope = factory.Services.CreateScope())
        {
            var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
            var root = await classrooms.CreateGroupAsync($"树根分组-{suffix}", null);
            var child = await classrooms.CreateGroupAsync($"树子分组-{suffix}", root.Id);
            var classroom = await classrooms.CreateAsync($"树内班级-{suffix}");
            await classrooms.CreateAsync($"树外班级-{suffix}");
            await classrooms.SetClassGroupsAsync(classroom.Id, [child.Id]);
            childId = child.Id;
        }

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Classes"));
        Assert.Contains("class-manager", html);
        Assert.Contains("data-class-batch-form", html);
        Assert.Contains("data-class-tree-toggle", html);
        Assert.Contains($"树根分组-{suffix}", html);
        Assert.Contains($"树子分组-{suffix}", html);

        var selectedHtml = WebUtility.HtmlDecode(await browser.GetStringAsync($"/Classes?groupId={childId}"));
        Assert.Contains($"<strong>树内班级-{suffix}</strong>", selectedHtml);
        Assert.DoesNotContain($"<strong>树外班级-{suffix}</strong>", selectedHtml);
    }

    // ---------- 批量语音广播 ----------

    /// <summary>
    /// 批量页的语音表单复用 voice-message.js：脚本把勾选目标按 data-voice-query 声明的键追加到上传地址。
    /// 这里按同样的方式上传一次，确保勾选班级真的进入广播目标，而不是被当成“没有勾选目标”。
    /// </summary>
    [Fact]
    public async Task BatchControlVoice_BroadcastsToCheckedClasses()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        using var client = factory.CreateClient();
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);

        // 先建分组，让“整组执行”的勾选框也渲染出来。
        await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "语音广播组" }));
        var classB = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
            new CreateClassRequest { Name = "语音广播二班" }))).Content.ReadFromJsonAsync<ClassDetail>())!;

        var html = await browser.GetStringAsync("/BatchControl");
        var classKey = VoiceQueryKey(html, "SelectedClassIds");
        var groupKey = VoiceQueryKey(html, "SelectedGroupIds");
        // 键名必须与服务端 OnPostBroadcastVoiceAsync 读取的查询键一致。
        Assert.Equal("classIds", classKey);
        Assert.Equal("groupIds", groupKey);

        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/BatchControl?handler=BroadcastVoice&{classKey}={classB.Id}");
        request.Headers.Add("X-CSRF-TOKEN", WebUtility.HtmlDecode(token));
        request.Content = new ByteArrayContent([0, 0]);
        request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var message = payload.GetProperty("message").GetString() ?? string.Empty;
        // 勾选的二班确实成为广播目标：它没有在线插件，因此逐台失败，而不是被当成“没有选择目标”整体拒绝。
        Assert.DoesNotContain("请先", message);
        Assert.Contains("语音广播二班", message);
        Assert.Contains("插件未在线", message);
    }

    private static string VoiceQueryKey(string html, string inputName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, $"name=\"{inputName}\"[^>]*data-voice-query=\"([^\"]+)\"");
        Assert.True(match.Success, $"勾选框 {inputName} 必须用 data-voice-query 声明上传键名");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Groups_SupportHierarchyAndMultiMembership()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();

        var root = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "某中学" }))).Content.ReadFromJsonAsync<ClassGroupInfo>())!;
        var child = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "高一年级", ParentId = root.Id }))).Content.ReadFromJsonAsync<ClassGroupInfo>())!;
        var grandchild = (await (await client.SendAsync(Bearer(HttpMethod.Post, "/api/class-groups", admin.AccessToken,
            new CreateClassGroupRequest { Name = "1 班组", ParentId = child.Id }))).Content.ReadFromJsonAsync<ClassGroupInfo>())!;

        var classA = (await CreateClassAsync(admin.AccessToken, "层级一班"))!;
        var classB = (await CreateClassAsync(admin.AccessToken, "层级二班"))!;

        // 同一个班级可属于多个分组。
        var multi = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classA.Id}/groups", admin.AccessToken,
            new UpdateClassGroupsRequest { GroupIds = [grandchild.Id, root.Id] }));
        Assert.Equal(HttpStatusCode.NoContent, multi.StatusCode);
        await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/groups", admin.AccessToken,
            new UpdateClassGroupsRequest { GroupIds = [grandchild.Id] }));

        // 按根分组批量开启访客：递归展开覆盖全部子分组中的班级。
        var batch = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest { GroupIds = [root.Id], Operation = "enableVisitor" }));
        batch.EnsureSuccessStatusCode();
        var details = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.All(new[] { classA.Id, classB.Id },
            id => Assert.True(details.Single(x => x.Id == id).VisitorEnabled));

        // 层级环被拒绝：把根分组移到自己的子分组之下。
        var cycle = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/class-groups/{root.Id}", admin.AccessToken,
            new UpdateClassGroupRequest { Name = "某中学", ParentId = grandchild.Id }));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        // 删除中间层：子分组上移到根，孙分组与班级归属不受影响。
        var removed = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/class-groups/{child.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var groups = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/class-groups", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassGroupInfo>>())!;
        Assert.Equal(root.Id, groups.Single(x => x.Id == grandchild.Id).ParentId);
    }

    [Fact]
    public async Task ClassInfo_ClassAdminCanRenameAndSetAvatar()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classB = (await CreateClassAsync(admin.AccessToken, "信息一班"))!;
        var adminUser = await CreateUserAsync("cls.info", "Cls-Info-Password-2026", AccountRole.ClassAdministratorId);
        await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = adminUser, RoleId = AccountRole.ClassAdministratorId }] }));

        var classAdmin = await _factory.LoginAsync("cls.info", "Cls-Info-Password-2026");
        var renamed = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/info", classAdmin.AccessToken,
            new UpdateClassRequest { Name = "信息一班（改）" }));
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        var details = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.Equal("信息一班（改）", details.Single(x => x.Id == classB.Id).Name);

        // 上传 PNG 头像后可匿名读取（访客页展示）。
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var uploadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/classes/{classB.Id}/avatar")
        {
            Headers = { Authorization = new("Bearer", classAdmin.AccessToken) },
            Content = new ByteArrayContent(png),
        };
        uploadRequest.Headers.TryAddWithoutValidation("X-Avatar-Type", "image/png");
        var upload = await client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        var anonymous = await _factory.CreateClient().GetAsync($"/api/classes/{classB.Id}/avatar");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal("image/png", anonymous.Content.Headers.ContentType?.MediaType);

        // 非图片内容被拒绝。
        var rejected = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/classes/{classB.Id}/avatar")
        {
            Headers =
            {
                Authorization = new("Bearer", classAdmin.AccessToken),
            },
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // 清除头像后 404。
        var cleared = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/classes/{classB.Id}/avatar", classAdmin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/classes/{classB.Id}/avatar")).StatusCode);

        // 普通成员不能改班名。
        var studentId = await CreateUserAsync("info.student", "Info-Student-Password-2026");
        await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = studentId, RoleId = AccountRole.StudentId }] }));
        var student = await _factory.LoginAsync("info.student", "Info-Student-Password-2026");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/info", student.AccessToken,
                new UpdateClassRequest { Name = "越权改名" }))).StatusCode);
    }

    [Fact]
    public async Task BatchImport_V2FormatAndPendingPasswordSetup()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();

        // v2 行格式：ID,用户名,班级,角色,密码（密码可空=待激活；班级不存在自动创建）。
        var importText = string.Join("\n",
            "imp.a,导入甲,导入一班,学生,",
            "imp.b,导入乙,导入一班,班主任,Imp-B-Password-2026",
            "# 注释行",
            "imp.c,导入丙,不存在的角色,");
        var imported = await client.SendAsync(Bearer(HttpMethod.Post, "/api/users/batch-import", admin.AccessToken,
            new BatchImportRequest
            {
                Text = importText,
                DefaultRoleId = AccountRole.StudentId,
            }));
        imported.EnsureSuccessStatusCode();
        var result = (await imported.Content.ReadFromJsonAsync<BatchImportResult>())!;
        Assert.Equal(2, result.Created);
        Assert.Single(result.Failures, x => x.Contains("imp.c"));

        // 待激活账号空密码登录 → 返回一次性设置令牌。
        var pending = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "imp.a",
            Password = "",
            DeviceName = "Integration Test",
        });
        pending.EnsureSuccessStatusCode();
        var pendingAuth = (await pending.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.True(pendingAuth.PasswordPending);
        Assert.False(string.IsNullOrEmpty(pendingAuth.SetupToken));

        // 已设密码账号空密码登录 → 正常失败。
        var notPending = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "imp.b",
            Password = "",
            DeviceName = "Integration Test",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, notPending.StatusCode);

        // 凭令牌设置密码后即可正常登录；班级与角色在导入时已就位。
        var setup = await client.PostAsJsonAsync("/api/auth/setup-password", new SetupPasswordRequest
        {
            Username = "imp.a",
            SetupToken = pendingAuth.SetupToken!,
            NewPassword = "Imp-A-Password-2026",
        });
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);
        var login = await _factory.LoginAsync("imp.a", "Imp-A-Password-2026");
        Assert.Contains(login.User!.Classes ?? [], x => x.Name == "导入一班" && x.RoleName == "学生");

        // 错误令牌被拒绝。
        var badSetup = await client.PostAsJsonAsync("/api/auth/setup-password", new SetupPasswordRequest
        {
            Username = "imp.b",
            SetupToken = "wrong-token",
            NewPassword = "Whatever-2026",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, badSetup.StatusCode);
    }

    // ---------- WebUI 班级切换 ----------

    [Fact]
    public async Task WebUi_SwitchClass_OnlyAllowsAccessibleClasses()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);
        await LoginWebUiAsync(browser);

        Guid classB;
        using (var scope = factory.Services.CreateScope())
        {
            classB = (await scope.ServiceProvider.GetRequiredService<ClassroomService>().CreateAsync("切换二班")).Id;
        }

        // 管理员可访问所有班级：切换写入 Cookie，并得到回跳。
        var accountHtml = await browser.GetStringAsync("/Account");
        Assert.Contains("aria-label=\"切换班级\"", accountHtml);
        var switched = await PostRazorFormAsync(browser, "/Account?handler=SwitchClass", accountHtml, new Dictionary<string, string>
        {
            ["classId"] = classB.ToString(),
            ["returnUrl"] = "/Account",
        });
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        var setCookies = switched.Headers.Contains("Set-Cookie")
            ? string.Join("|", switched.Headers.GetValues("Set-Cookie"))
            : string.Empty;
        Assert.Contains(WebPageModel.CurrentClassCookie, setCookies);

        // 普通学生只有默认班级：看不到切换器，切换到自己不属于的班级不会写 Cookie。
        await CreateUserAsync("switch.student", "Switch-Student-Password-2026", factory: factory);
        using var studentBrowser = CreateBrowser(factory);
        await LoginWebUiAsync(studentBrowser, "switch.student", "Switch-Student-Password-2026");
        var studentAccount = await studentBrowser.GetStringAsync("/Account");
        Assert.DoesNotContain("aria-label=\"切换班级\"", studentAccount);
        var deniedSwitch = await PostRazorFormAsync(studentBrowser, "/Account?handler=SwitchClass", studentAccount, new Dictionary<string, string>
        {
            ["classId"] = classB.ToString(),
            ["returnUrl"] = "/Account",
        });
        Assert.Equal(HttpStatusCode.Redirect, deniedSwitch.StatusCode);
        var studentCookies = deniedSwitch.Headers.Contains("Set-Cookie")
            ? string.Join("|", deniedSwitch.Headers.GetValues("Set-Cookie"))
            : string.Empty;
        Assert.DoesNotContain(WebPageModel.CurrentClassCookie, studentCookies);
    }

    // ---------- 辅助 ----------

    private static HttpClient CreateBrowser(TestWebApplicationFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private static async Task LoginWebUiAsync(
        HttpClient browser,
        string username = TestWebApplicationFactory.AdminUsername,
        string password = TestWebApplicationFactory.AdminPassword)
    {
        var html = await browser.GetStringAsync("/Login");
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRazorFormAsync(
        HttpClient browser,
        string path,
        string html,
        IReadOnlyDictionary<string, string> values)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Razor 表单页必须包含 CSRF 令牌");
        var fields = new Dictionary<string, string>(values)
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        };
        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private async Task<ClassDetail?> CreateClassAsync(string token, string name)
    {
        var response = await _factory.CreateClient().SendAsync(Bearer(
            HttpMethod.Post, "/api/classes", token, new CreateClassRequest { Name = name }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClassDetail>();
    }

    private async Task<Guid> CreateUserAsync(
        string username, string password, Guid? roleId = null, TestWebApplicationFactory? factory = null)
    {
        var target = factory ?? _factory;
        var admin = await target.LoginAsync();
        var response = await target.CreateClient().SendAsync(Bearer(
            HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest
            {
                ClassId = TestWebApplicationFactory.DefaultClassId,
                Username = username,
                DisplayName = username,
                Password = password,
                RoleId = roleId,
            }));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<UserListItem>())!;
        return created.Id;
    }

    private async Task<WebSocket> ConnectPluginAsync()
    {
        var token = await _factory.GetPluginTokenAsync();
        return await ConnectAsync(token);
    }

    private async Task<WebSocket> ConnectWatchAsync(
        string username = TestWebApplicationFactory.AdminUsername,
        string password = TestWebApplicationFactory.AdminPassword)
    {
        var login = await _factory.LoginAsync(username, password);
        return await ConnectAsync(login.AccessToken);
    }

    private async Task<WebSocket> ConnectAsync(string token)
    {
        var socketClient = _factory.Server.CreateWebSocketClient();
        return await socketClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"),
            CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<Envelope> ReceiveEnvelopeAsync(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(message.ToArray()), JsonDefaults.Options)!;
            if (envelope.Type == expectedType) return envelope;
        }
    }

    private static async Task<T> ReceivePayloadAsync<T>(WebSocket socket, string expectedType)
    {
        var envelope = await ReceiveEnvelopeAsync(socket, expectedType);
        return JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
