using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>班级自治策略：系统管理员统一决定班主任能否改名、改头像、拉取课表，并逐个插件开放扩展设置。</summary>
public sealed class ClassSelfServiceTests
{
    [Fact]
    public async Task Policy_DefaultsKeepLegacyRightsAndKeepsExtensionSettingsForAdmins()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using var scope = factory.Services.CreateScope();

        var policy = await scope.ServiceProvider.GetRequiredService<ClassSelfServiceSettings>().GetAsync();

        Assert.Equal(ClassSelfServicePolicy.Default, policy);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ExtensionGroupPolicyService>().ListClassAdminAllowedAsync());
    }

    [Fact]
    public async Task ClassAdmin_RenameAndAvatar_FollowUnifiedPolicy()
    {
        await using var factory = new TestWebApplicationFactory();
        var (admin, classId, classAdmin) = await CreateClassWithClassAdminAsync(factory, "自治一班", "self.rename");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/classes/{classId}/info", classAdmin, new UpdateClassRequest { Name = "自治一班（班主任改）" }))).StatusCode);

        await SetPolicyAsync(factory, ClassSelfServicePolicy.Default with { CanRename = false, CanChangeAvatar = false });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/classes/{classId}/info", classAdmin, new UpdateClassRequest { Name = "越权改名" }))).StatusCode);
        var upload = Bearer(HttpMethod.Put, $"/api/classes/{classId}/avatar", classAdmin);
        upload.Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        upload.Headers.TryAddWithoutValidation("X-Avatar-Type", "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(upload)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/classes/{classId}/avatar", classAdmin))).StatusCode);

        // 系统管理员不受班级自治策略限制。
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/classes/{classId}/info", admin, new UpdateClassRequest { Name = "自治一班（管理员改）" }))).StatusCode);
    }

    [Fact]
    public async Task SchedulePullInterval_IsSystemAdminOnlyGlobalSetting()
    {
        await using var factory = new TestWebApplicationFactory();
        var (admin, _, classAdmin) = await CreateClassWithClassAdminAsync(factory, "拉取一班", "self.pull");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/settings/schedule-pull", classAdmin, new { intervalMinutes = 60 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/settings/schedule-pull", admin, new { intervalMinutes = 60 }))).StatusCode);
    }

    [Fact]
    public async Task ExtensionSettings_ClassAdminNeedsPerPluginAccessAndExtensionPermission()
    {
        await using var factory = new TestWebApplicationFactory();
        var (adminToken, classId, classAdminToken) = await CreateClassWithClassAdminAsync(factory, "扩展一班", "self.extension");
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var groups = scope.ServiceProvider.GetRequiredService<ExtensionGroupService>();
        var classAdmin = (await identities.ListUsersAsync()).Single(x => x.Username == "self.extension");
        var profile = (await identities.GetProfileAsync(classAdmin.Id))!;

        Assert.False(await groups.CanEditSettingsAsync(profile, classId, "demo.settings"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/classes/{classId}/extension-groups/demo.settings/settings", classAdminToken,
            new { values = new Dictionary<string, string?> { ["volume"] = "30" } }))).StatusCode);
        Assert.Empty((await groups.ListClassAdminEditableAsync(
            profile.Id, profile.Role, profile.GrantedPermissions, classId))!);

        // 只有系统管理员可以开放插件，且只开放被点名的插件。
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/extension-groups/demo.settings/class-admin-access", classAdminToken, new { allowClassAdmin = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/extension-groups/demo.settings/class-admin-access", adminToken, new { allowClassAdmin = true }))).StatusCode);
        Assert.True(await groups.CanEditSettingsAsync(profile, classId, "demo.settings"));
        Assert.False(await groups.CanEditSettingsAsync(profile, classId, "demo.other"));
        Assert.Equal(["demo.settings"], (await groups.ListClassAdminEditableAsync(
            profile.Id, profile.Role, profile.GrantedPermissions, classId))!);

        // 权限复核通过后，班级未上报该分组时返回明确的业务错误，而不是越权。
        var missing = await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/classes/{classId}/extension-groups/demo.settings/settings", classAdminToken,
            new { values = new Dictionary<string, string?> { ["volume"] = "30" } }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("没有上报该扩展分组", (await missing.Content.ReadFromJsonAsync<CommandResult>())!.Message);

        // 班主任仍只能修改自己担任班主任的班级。
        var other = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", adminToken,
            new CreateClassRequest { Name = "扩展二班" }));
        other.EnsureSuccessStatusCode();
        var otherClass = (await other.Content.ReadFromJsonAsync<ClassDetail>())!;
        Assert.False(await groups.CanEditSettingsAsync(profile, otherClass.Id, "demo.settings"));

        // 收回后班主任重新失去该插件的管理权限。
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/extension-groups/demo.settings/class-admin-access", adminToken, new { allowClassAdmin = false }))).StatusCode);
        Assert.False(await groups.CanEditSettingsAsync(profile, classId, "demo.settings"));
    }

    [Fact]
    public async Task ExtensionSettingsPage_ClassAdminSeesOnlyOpenedPluginsInSidebar()
    {
        await using var factory = new TestWebApplicationFactory();
        var (adminToken, classId, _) = await CreateClassWithClassAdminAsync(factory, "侧栏一班", "self.sidebar");
        using (var scope = factory.Services.CreateScope())
        {
            var setting = new ExtensionParameter { Key = "volume", Label = "音量", Type = ExtensionParameterType.Number };
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveExtensionGroups(classId, new[]
            {
                new ExtensionGroupDefinition { Id = "demo.open", DisplayName = "开放插件", Settings = [setting] },
                new ExtensionGroupDefinition { Id = "demo.closed", DisplayName = "统一管理插件", Settings = [setting] },
            });
        }
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginWebUiAsync(browser, "self.sidebar", ClassAdminPassword);
        await SelectClassAsync(browser, classId);

        // 默认不开放任何插件：侧栏没有入口，直接访问也被拒绝。
        Assert.DoesNotContain("href=\"/ExtensionSettings\"", await browser.GetStringAsync("/Schedule"));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/ExtensionSettings")).StatusCode);

        using (var api = factory.CreateClient())
            Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(Bearer(HttpMethod.Put,
                "/api/extension-groups/demo.open/class-admin-access", adminToken, new { allowClassAdmin = true }))).StatusCode);

        Assert.Contains("href=\"/ExtensionSettings\"", await browser.GetStringAsync("/Schedule"));
        var list = WebUtility.HtmlDecode(await browser.GetStringAsync("/ExtensionSettings"));
        Assert.Contains("开放插件", list);
        Assert.DoesNotContain("统一管理插件", list);
        Assert.Contains("保存到当前班级", await browser.GetStringAsync("/ExtensionSettings?groupId=demo.open"));
        Assert.Contains("没有找到该插件的设置", WebUtility.HtmlDecode(
            await browser.GetStringAsync("/ExtensionSettings?groupId=demo.closed")));
    }

    private static async Task SelectClassAsync(HttpClient browser, Guid classId)
    {
        var html = await browser.GetStringAsync("/Schedule");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(token.Success, "页面必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Schedule?handler=SwitchClass", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["classId"] = classId.ToString(), ["returnUrl"] = "/Schedule",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private const string ClassAdminPassword = "Self-Service-Password-2026";

    private static async Task LoginWebUiAsync(HttpClient browser, string username, string password)
    {
        var html = await browser.GetStringAsync("/Login");
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task PolicyApi_IsReadableByAccountsAndWritableOnlyByAdmins()
    {
        await using var factory = new TestWebApplicationFactory();
        var (admin, _, classAdmin) = await CreateClassWithClassAdminAsync(factory, "接口一班", "self.api");
        using var client = factory.CreateClient();
        var changed = new { canRename = false, canChangeAvatar = true, canPullSchedule = true };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put,
            "/api/settings/class-self-service", classAdmin, changed))).StatusCode);
        var saved = await client.SendAsync(Bearer(HttpMethod.Put, "/api/settings/class-self-service", admin, changed));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/settings/class-self-service", classAdmin)))
            .Content.ReadFromJsonAsync<ClassSelfServicePolicy>();
        Assert.Equal(new ClassSelfServicePolicy(false, true, true), read);
    }

    private static async Task SetPolicyAsync(TestWebApplicationFactory factory, ClassSelfServicePolicy policy)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ClassSelfServiceSettings>().SetAsync(policy);
    }

    /// <summary>创建班级并把新账号以班主任身份加入，返回管理员令牌、班级 Id 与班主任令牌。</summary>
    private static async Task<(string Admin, Guid ClassId, string ClassAdmin)> CreateClassWithClassAdminAsync(
        TestWebApplicationFactory factory, string className, string username)
    {
        const string password = ClassAdminPassword;
        var admin = (await factory.LoginAsync()).AccessToken;
        using var client = factory.CreateClient();
        var created = await client.SendAsync(Bearer(HttpMethod.Post, "/api/classes", admin,
            new CreateClassRequest { Name = className }));
        created.EnsureSuccessStatusCode();
        var classroom = (await created.Content.ReadFromJsonAsync<ClassDetail>())!;
        var user = await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin, new CreateUserRequest
        {
            Username = username,
            DisplayName = username,
            Password = password,
            RoleId = AccountRole.ClassAdministratorId,
        }));
        user.EnsureSuccessStatusCode();
        var userId = (await user.Content.ReadFromJsonAsync<UserListItem>())!.Id;
        (await client.SendAsync(Bearer(HttpMethod.Put, $"/api/classes/{classroom.Id}/members", admin,
            new UpdateClassMembersRequest
            {
                Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.ClassAdministratorId }],
            }))).EnsureSuccessStatusCode();
        return (admin, classroom.Id, (await factory.LoginAsync(username, password)).AccessToken);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
