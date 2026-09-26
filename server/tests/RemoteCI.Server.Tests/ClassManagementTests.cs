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
        var classA = Classroom.DefaultId;
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
    public async Task ClassCrud_OnlyAdminCanManageAndDefaultClassIsProtected()
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

        // 空名拒绝；默认班级不可删除。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin.AccessToken,
                new CreateClassRequest { Name = " " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/classes/{Classroom.DefaultId}", admin.AccessToken))).StatusCode);

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
                ClassIds = [classA.Id, classB.Id, Classroom.DefaultId],
                Operation = "enableVisitor",
            }));
        enable.EnsureSuccessStatusCode();
        var enableResult = (await enable.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.All(enableResult.Results, x => Assert.True(x.Success));
        var listed = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.Equal(3, listed.Count(x => x.VisitorEnabled));

        var disable = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest
            {
                ClassIds = [classA.Id, classB.Id],
                Operation = "disableVisitor",
            }));
        disable.EnsureSuccessStatusCode();
        var afterDisable = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.Single(afterDisable.Where(x => x.VisitorEnabled));

        // 默认班级删除失败但不影响其他班级；未知操作被拒绝。
        var batchDelete = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes/batch", admin.AccessToken,
            new BatchClassOperationRequest
            {
                ClassIds = [classA.Id, Classroom.DefaultId],
                Operation = "delete",
            }));
        batchDelete.EnsureSuccessStatusCode();
        var deleteResult = (await batchDelete.Content.ReadFromJsonAsync<BatchClassOperationResult>())!;
        Assert.Equal(2, deleteResult.Results.Count);
        Assert.Contains(deleteResult.Results, x => x.ClassId == classA.Id && x.Success);
        Assert.Contains(deleteResult.Results, x => x.ClassId == Classroom.DefaultId && !x.Success);
        var afterDelete = (await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        Assert.DoesNotContain(afterDelete, x => x.Id == classA.Id);
        Assert.Contains(afterDelete, x => x.Id == Classroom.DefaultId);
    }

    // ---------- 成员管理与按班级权限隔离 ----------

    [Fact]
    public async Task Members_ClassScopedAccess_IsEnforcedPerClass()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var classB = (await CreateClassAsync(admin.AccessToken, "隔离一班"))!;

        var userId = await CreateUserAsync("iso.student", "Iso-Student-Password-2026");

        // 新用户自动进入默认班级；把 TA 加进 B 班。
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
        Assert.Contains(summaries, x => x.Id == Classroom.DefaultId);
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

        // 班管理员默认就是默认班级的成员；同时成为 B 班班管理员。
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

        // 先把 B 班成员设为该用户，再把默认班级成员清空，使该用户只属于 B 班。
        var putB = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classB.Id}/members", admin.AccessToken,
            new UpdateClassMembersRequest { Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.StudentId }] }));
        Assert.Equal(HttpStatusCode.NoContent, putB.StatusCode);
        var clearDefault = await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{Classroom.DefaultId}/members", admin.AccessToken,
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
