using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>权限细化：角色默认权限 + 个人新增/收回、拆出的独立权限、系统管理权限与授权边界。</summary>
public sealed class PermissionRefinementTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public PermissionRefinementTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void Effective_AppliesPersonalGrantsAndRevocationsOnTopOfRoleDefaults()
    {
        var effective = RolePermissions.Effective(
            UserRole.User,
            UserPermissions.CaptureScreen,
            UserPermissions.SendNotifications | UserPermissions.PowerControl,
            UserPermissions.PowerControl);

        Assert.True(effective.HasFlag(UserPermissions.ViewCurrentCourse));
        Assert.True(effective.HasFlag(UserPermissions.SendNotifications));
        Assert.True(effective.HasFlag(UserPermissions.CaptureScreen));
        Assert.False(effective.HasFlag(UserPermissions.PowerControl));
        // 查看课表是基础权限，不能被收回；管理员始终拥有全部权限。
        Assert.True(RolePermissions.Effective(UserRole.User, 0, 0, UserPermissions.ViewCurrentCourse)
            .HasFlag(UserPermissions.ViewCurrentCourse));
        Assert.Equal(UserPermissions.All, RolePermissions.Effective(UserRole.Admin, 0, 0, UserPermissions.All));
        Assert.Equal(ManagementPermissions.All,
            RolePermissions.EffectiveManagement(UserRole.Admin, 0, 0, ManagementPermissions.All));
    }

    [Fact]
    public void Adjust_StoresOnlyTheDifferenceFromTheRole()
    {
        var (granted, revoked) = RolePermissions.Adjust(
            UserPermissions.SendNotifications | UserPermissions.PowerControl,
            UserPermissions.SendNotifications | UserPermissions.CaptureScreen);
        Assert.Equal(UserPermissions.CaptureScreen, granted);
        Assert.Equal(UserPermissions.PowerControl, revoked);

        var (grantedManagement, revokedManagement) = RolePermissions.Adjust(
            ManagementPermissions.ManageHolidays, ManagementPermissions.ManageHolidays | ManagementPermissions.ViewDevices);
        Assert.Equal(ManagementPermissions.ViewDevices, grantedManagement);
        Assert.Equal(ManagementPermissions.None, revokedManagement);
    }

    [Fact]
    public void Catalog_ListsEveryAssignablePermissionExactlyOnce()
    {
        var items = PermissionCatalog.Groups.SelectMany(x => x.Items).ToList();
        Assert.Equal(RolePermissions.Assignable & ~PermissionCatalog.Hidden, PermissionCatalog.ListedPermissions);
        Assert.Equal(ManagementPermissions.All, PermissionCatalog.ListedManagement);
        Assert.Equal(items.Count, items.Select(x => (x.IsManagement, x.Value)).Distinct().Count());
        Assert.All(PermissionCatalog.Groups, group => Assert.False(string.IsNullOrWhiteSpace(group.IconSvg)));
        Assert.DoesNotContain(items, x => x.Permission == UserPermissions.ChangeDisplayName);
    }

    [Theory]
    [InlineData(CommandKind.Volume, UserPermissions.VolumeControl)]
    [InlineData(CommandKind.ClearNotifications, UserPermissions.ClearNotifications)]
    [InlineData(CommandKind.ApplyMainWindowLayout, UserPermissions.MainWindowLayout)]
    [InlineData(CommandKind.ExecuteTerminalCommand, UserPermissions.RemoteTerminal)]
    [InlineData(CommandKind.UpgradeClassIsland, UserPermissions.UpgradeSoftware)]
    [InlineData(CommandKind.InstallPlugins, UserPermissions.ManagePlugins)]
    [InlineData(CommandKind.RestartClassIsland, UserPermissions.ControlClassIsland)]
    [InlineData(CommandKind.LaunchClassIsland, UserPermissions.ControlClassIsland)]
    [InlineData(CommandKind.StopAppGuard, UserPermissions.ManageDeviceGuard)]
    [InlineData(CommandKind.ApplyExtensionSettings, UserPermissions.ConfigureExtensions)]
    [InlineData(CommandKind.DistributeProfile, UserPermissions.ManageClassProfile)]
    [InlineData(CommandKind.ReadProfile, UserPermissions.ManageClassProfile)]
    [InlineData(CommandKind.Power, UserPermissions.PowerControl)]
    public void Commands_RequireTheirOwnPermission(CommandKind command, UserPermissions required) =>
        Assert.Equal(required, CommandPermissions.Required(command));

    [Fact]
    public void ForDispatch_AddsLegacyBitsOnlyWhenTheNewPermissionIsHeld()
    {
        Assert.True(CommandPermissions.ForDispatch(CommandKind.Volume, UserPermissions.VolumeControl)
            .HasFlag(UserPermissions.PowerControl));
        Assert.Equal(UserPermissions.PowerControl,
            CommandPermissions.ForDispatch(CommandKind.Volume, UserPermissions.PowerControl));
        Assert.True(CommandPermissions.ForDispatch(CommandKind.ExecuteTerminalCommand, UserPermissions.RemoteTerminal)
            .HasFlag(UserPermissions.ManageUsers));
        Assert.Equal(UserPermissions.ManageUsers,
            CommandPermissions.ForDispatch(CommandKind.ExecuteTerminalCommand, UserPermissions.ManageUsers));
        Assert.Equal(UserPermissions.SendNotifications,
            CommandPermissions.ForDispatch(CommandKind.SendNotification, UserPermissions.SendNotifications));
    }

    [Fact]
    public void Upgrade_KeepsWhatOldPermissionsAllowed()
    {
        var split = PermissionUpgrade.Split(UserPermissions.SendNotifications | UserPermissions.PowerControl |
            UserPermissions.MainMenuControl | UserPermissions.ManageUsers);
        Assert.True(split.HasFlag(UserPermissions.ClearNotifications));
        Assert.True(split.HasFlag(UserPermissions.VolumeControl));
        Assert.True(split.HasFlag(UserPermissions.MainWindowLayout));
        Assert.True(split.HasFlag(UserPermissions.RemoteTerminal | UserPermissions.ManagePlugins | UserPermissions.UpgradeSoftware |
            UserPermissions.ControlClassIsland | UserPermissions.ManageDeviceGuard | UserPermissions.ManageClassProfile));
        Assert.Equal(PermissionUpgrade.ManagementFromManageUsers, PermissionUpgrade.Management(UserPermissions.ManageUsers));
        Assert.Equal(UserPermissions.SendVoiceMessages, PermissionUpgrade.Split(UserPermissions.SendVoiceMessages));

        var legacyClassHead = UserPermissions.ManageSchedule | UserPermissions.RunExtensions;
        var classHead = PermissionUpgrade.ClassAdministrator(legacyClassHead, new ClassSelfServicePolicy(true, false, false));
        Assert.True(classHead.HasFlag(UserPermissions.ManageClassInfo));
        Assert.False(classHead.HasFlag(UserPermissions.PullSchedule));
        Assert.True(classHead.HasFlag(UserPermissions.ConfigureExtensions | UserPermissions.ManageClassProfile |
            UserPermissions.ManageClassBoards | UserPermissions.ViewAuditLog | UserPermissions.ViewLessonStats));
    }

    [Fact]
    public async Task BuiltInClassAdministratorRoleCarriesClassHeadPermissions()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = await scope.ServiceProvider.GetRequiredService<AccountRoleService>().ListAsync();
        var classHead = roles.Single(x => x.Id == AccountRole.ClassAdministratorId).DefaultPermissions;
        Assert.True(classHead.HasFlag(UserPermissions.ManageClassInfo | UserPermissions.PullSchedule |
            UserPermissions.ConfigureExtensions | UserPermissions.ManageClassProfile | UserPermissions.ManageClassBoards |
            UserPermissions.ViewAuditLog | UserPermissions.ViewLessonStats));
        Assert.False(classHead.HasFlag(UserPermissions.RemoteTerminal));
        Assert.Equal(ManagementPermissions.All,
            roles.Single(x => x.Id == AccountRole.AdministratorId).DefaultManagementPermissions);
    }

    [Fact]
    public async Task RevokedRolePermissionIsRemovedInClassAndSurvivesOldClientUpdates()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<AccountRoleService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var access = scope.ServiceProvider.GetRequiredService<ClassAccessService>();
        var role = await roles.CreateAsync("Refine Revoke", UserPermissions.SendNotifications | UserPermissions.PowerControl);
        var user = await identities.CreateUserAsync(new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "refine.revoke", DisplayName = "Refine Revoke", Password = "Refine-Revoke-2026",
            RoleId = role.Id, RevokedPermissions = UserPermissions.PowerControl,
        });

        var inClass = await access.GetEffectivePermissionsAsync(user.Id, UserRole.User, TestWebApplicationFactory.DefaultClassId);
        Assert.True(inClass.HasFlag(UserPermissions.SendNotifications));
        Assert.False(inClass.HasFlag(UserPermissions.PowerControl));

        // 旧客户端不下发 revokedPermissions：保持原有的收回，不被一次编辑清空。
        await identities.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            DisplayName = "Refine Revoke", Role = UserRole.User, RoleId = role.Id, Enabled = true,
        });
        var listed = (await identities.ListUsersAsync()).Single(x => x.Id == user.Id);
        Assert.Equal(UserPermissions.PowerControl, listed.RevokedPermissions);
        Assert.False(listed.EffectivePermissions.HasFlag(UserPermissions.PowerControl));

        // 角色新增的权限对未单独调整的账号直接生效。
        await roles.UpdateAsync(role.Id, role.Name,
            UserPermissions.SendNotifications | UserPermissions.PowerControl | UserPermissions.CaptureScreen);
        Assert.True((await access.GetEffectivePermissionsAsync(user.Id, UserRole.User, TestWebApplicationFactory.DefaultClassId))
            .HasFlag(UserPermissions.CaptureScreen));
    }

    [Fact]
    public async Task NonAdminUserManagerCannotGrantPermissionsTheyLack()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var managerRole = await CreateRoleAsync(client, admin.AccessToken, "Refine Manager",
            UserPermissions.ManageUsers | UserPermissions.SendNotifications);
        var manager = await CreateUserAsync(client, admin.AccessToken, "refine.manager", managerRole);
        var target = await CreateUserAsync(client, admin.AccessToken, "refine.target", AccountRole.StudentId);
        var managerToken = (await _factory.LoginAsync("refine.manager", "Refine-Pass-2026")).AccessToken;

        var update = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/users/{target.Id}", managerToken,
            new UpdateUserRequest
            {
                DisplayName = "refine.target", Role = UserRole.User, RoleId = AccountRole.StudentId, Enabled = true,
                GrantedPermissions = UserPermissions.SendNotifications | UserPermissions.RemoteTerminal,
                GrantedManagementPermissions = ManagementPermissions.ManageClasses,
            }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<UserListItem>(JsonDefaults.Options))!;
        Assert.True(updated.EffectivePermissions.HasFlag(UserPermissions.SendNotifications));
        Assert.False(updated.EffectivePermissions.HasFlag(UserPermissions.RemoteTerminal));
        Assert.False(updated.EffectiveManagementPermissions.HasFlag(ManagementPermissions.ManageClasses));
        Assert.NotEqual(Guid.Empty, manager.Id);
    }

    [Fact]
    public async Task ManagementPermissionOpensItsSystemEndpointOnly()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var role = await CreateRoleAsync(client, admin.AccessToken, "Refine Registrar", UserPermissions.AccessWebUi,
            ManagementPermissions.ManageClasses);
        await CreateUserAsync(client, admin.AccessToken, "refine.registrar", role);
        await CreateUserAsync(client, admin.AccessToken, "refine.plain", AccountRole.StudentId);
        var registrar = (await _factory.LoginAsync("refine.registrar", "Refine-Pass-2026")).AccessToken;
        var plain = (await _factory.LoginAsync("refine.plain", "Refine-Pass-2026")).AccessToken;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/class-groups", registrar))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/class-groups", plain))).StatusCode);
        // 班级管理不包含角色配置，也不能把成员设为班内管理员。
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            "/api/roles", registrar, new CreateAccountRoleRequest { Name = "Nope" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put,
            $"/api/classes/{TestWebApplicationFactory.DefaultClassId}/members", registrar, new UpdateClassMembersRequest
            {
                Members = [new ClassMemberInput { UserId = Guid.NewGuid(), RoleId = AccountRole.AdministratorId }],
            }))).StatusCode);
    }

    [Fact]
    public async Task UsersPageRendersGroupedChecklistAndSavesUncheckedDefaultsAsRevocations()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<AccountRoleService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var role = await roles.CreateAsync("Refine Page", UserPermissions.SendNotifications | UserPermissions.VolumeControl);
        var user = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "refine.page", DisplayName = "Refine Page", Password = "Refine-Pass-2026", RoleId = role.Id,
        });

        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/Users");
        Assert.Contains("data-permission-checklist", html);
        Assert.Contains("name=\"Edit.Management\"", html);
        var createRole = html.IndexOf("id=\"role-create-dialog\"", StringComparison.Ordinal);
        var createRoleDialog = html[createRole..html.IndexOf("</dialog>", createRole, StringComparison.Ordinal)];
        Assert.Equal(PermissionCatalog.Groups.Count, Regex.Matches(createRoleDialog, "data-permission-group ").Count);
        Assert.Contains("教室电脑", WebUtility.HtmlDecode(html));

        // 只勾选“发送通知”并新增“屏幕截图”：角色默认的“音量与静音”被记为收回。
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value);
        var response = await browser.PostAsync("/Users?handler=Update", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Edit.Id", user.Id.ToString()),
            new("Edit.DisplayName", "Refine Page"),
            new("Edit.RoleId", role.Id.ToString()),
            new("Edit.Enabled", "true"),
            new("Edit.Permission", ((int)UserPermissions.SendNotifications).ToString()),
            new("Edit.Permission", ((int)UserPermissions.CaptureScreen).ToString()),
            new("Edit.Management", ((int)ManagementPermissions.ViewDevices).ToString()),
        }));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther, response.StatusCode.ToString());
        var saved = (await identities.ListUsersAsync()).Single(x => x.Id == user.Id);
        Assert.Equal(UserPermissions.CaptureScreen, saved.GrantedPermissions);
        Assert.Equal(UserPermissions.VolumeControl, saved.RevokedPermissions);
        Assert.Equal(ManagementPermissions.ViewDevices, saved.GrantedManagementPermissions);
    }

    private static async Task<Guid> CreateRoleAsync(
        HttpClient client, string token, string name, UserPermissions permissions,
        ManagementPermissions management = ManagementPermissions.None)
    {
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/roles", token,
            new CreateAccountRoleRequest { Name = name, DefaultPermissions = permissions, DefaultManagementPermissions = management }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountRoleInfo>(JsonDefaults.Options))!.Id;
    }

    private static async Task<UserListItem> CreateUserAsync(HttpClient client, string token, string username, Guid roleId)
    {
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", token,
            new CreateUserRequest
            {
                Username = username, DisplayName = username, Password = "Refine-Pass-2026", RoleId = roleId,
                ClassId = TestWebApplicationFactory.DefaultClassId,
            }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserListItem>(JsonDefaults.Options))!;
    }

    private static async Task LoginWebUiAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value);
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }
}
