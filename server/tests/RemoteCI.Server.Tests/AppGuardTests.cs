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

public sealed class AppGuardTests
{
    [Fact]
    public async Task EffectivePolicy_UsesGlobalUntilDeviceOverrideIsSet_AndCarriesOrganizationName()
    {
        await using var factory = new TestWebApplicationFactory();
        var credentialId = await PluginCredentialIdAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guard = scope.ServiceProvider.GetRequiredService<AppGuardService>();
        (await db.SystemMetadata.SingleAsync(x => x.Id == 1)).OrganizationName = "实验中学信息中心";
        await db.SaveChangesAsync();

        await guard.SetGlobalAsync(new AppGuardPolicy
        {
            Enabled = true, RestartAfterExit = true, RestartAfterExitDelayMinutes = 40, RestartOnCrash = true, PreventDeletion = true,
        });
        var inherited = await guard.GetEffectiveAsync(credentialId);
        Assert.True(inherited.Enabled);
        Assert.Equal(40, inherited.RestartAfterExitDelayMinutes);
        Assert.Equal("实验中学信息中心", inherited.OrganizationName);
        Assert.Equal("退出 40 分钟后自启 / 意外退出时重启 / 禁止删除", inherited.Describe());

        var view = await guard.SetOverrideAsync(credentialId, new AppGuardPolicy { Enabled = false, RestartAfterExitDelayMinutes = 99999 });
        Assert.True(view.Overridden);
        Assert.False((await guard.GetEffectiveAsync(credentialId)).Enabled);
        Assert.Equal(AppGuardPolicy.MaxRestartDelayMinutes, view.Policy.RestartAfterExitDelayMinutes);

        var cleared = await guard.ClearOverrideAsync(credentialId);
        Assert.False(cleared.Overridden);
        Assert.True((await guard.GetEffectiveAsync(credentialId)).Enabled);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => guard.GetDeviceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RecordEvents_StoresEvents_AndNotifiesAdminsOnlyAboutRestores()
    {
        await using var factory = new TestWebApplicationFactory();
        var credentialId = await PluginCredentialIdAsync(factory);
        using var scope = factory.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<AppGuardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await guard.RecordEventsAsync(credentialId, null, new AppGuardEventBatch
        {
            Events =
            [
                new AppGuardEvent { Kind = AppGuardEventKinds.CrashRestarted, AtUnixMs = 1_700_000_000_000, Detail = "未收到正常退出标记" },
                new AppGuardEvent { Kind = AppGuardEventKinds.Restored, AtUnixMs = 1_700_000_100_000, Detail = "已恢复 12 个文件并重新启动" },
                new AppGuardEvent { Kind = " ", AtUnixMs = 1 },
            ],
        });

        var events = await guard.GetRecentEventsAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(AppGuardEventKinds.Restored, events[0].Kind);
        Assert.Equal("已自动恢复", events[0].KindName);
        var notifications = await db.UserNotifications.AsNoTracking().Where(x => x.Kind == UserNotificationKinds.AppGuard).ToListAsync();
        var notice = Assert.Single(notifications);
        Assert.Contains("已自动恢复", notice.Title);
        Assert.Equal("/AppGuard", notice.Link);
    }

    [Fact]
    public async Task RestEndpoints_AreAdminOnly_AndRoundTripGlobalAndDevicePolicies()
    {
        await using var factory = new TestWebApplicationFactory();
        var credentialId = await PluginCredentialIdAsync(factory);
        var admin = await factory.LoginAsync();
        var client = factory.CreateClient();

        var put = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/settings/app-guard", admin.AccessToken,
            new AppGuardPolicy { Enabled = true, RestartAfterExit = true, RestartAfterExitDelayMinutes = 40, BackupIntervalHours = 0 }));
        put.EnsureSuccessStatusCode();
        var global = (await put.Content.ReadFromJsonAsync<AppGuardPolicy>())!;
        Assert.True(global.Enabled);
        Assert.Equal(AppGuardPolicy.MinBackupIntervalHours, global.BackupIntervalHours);

        var device = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/devices/{credentialId}/app-guard", admin.AccessToken,
            new AppGuardPolicy { Enabled = true, PreventDeletion = true }));
        device.EnsureSuccessStatusCode();
        Assert.True((await device.Content.ReadFromJsonAsync<AppGuardDevicePolicyView>())!.Overridden);
        var missing = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/devices/{Guid.NewGuid()}/app-guard", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var reset = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Delete, $"/api/devices/{credentialId}/app-guard", admin.AccessToken));
        Assert.False((await reset.Content.ReadFromJsonAsync<AppGuardDevicePolicyView>())!.Overridden);

        using (var scope = factory.Services.CreateScope())
        {
            var role = await scope.ServiceProvider.GetRequiredService<AccountRoleService>().CreateAsync("守护测试", UserPermissions.ApiAccess | UserPermissions.AccessWebUi);
            await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().CreateUserAsync(new CreateUserRequest
            {
                ClassId = TestWebApplicationFactory.DefaultClassId,
                Username = "guard.user",
                DisplayName = "guard.user",
                Password = "Guard-User-Password-2026",
                RoleId = role.Id,
            });
        }
        var user = await factory.LoginAsync("guard.user", "Guard-User-Password-2026");
        var denied = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/settings/app-guard", user.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedEvents = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/app-guard/events", user.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, deniedEvents.StatusCode);
    }

    [Fact]
    public async Task AdminPage_ShowsPolicyDevicesAndOrganizationNotice()
    {
        await using var factory = new TestWebApplicationFactory();
        await PluginCredentialIdAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.SystemMetadata.SingleAsync(x => x.Id == 1)).OrganizationName = "实验中学";
            await db.SaveChangesAsync();
        }
        using var browser = await LoggedInBrowserAsync(factory);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/AppGuard"));

        Assert.Contains("<h1>应用守护</h1>", html);
        Assert.Contains("当前ClassIsland由实验中学统一管理，请勿自行删除，当前实例已由RemoteCI自动恢复。", html);
        Assert.Contains("沿用全局", html);
        Assert.Contains("<span>应用守护</span>", html);
        var config = WebUtility.HtmlDecode(await browser.GetStringAsync("/SystemConfig"));
        Assert.Contains("组织信息", config);
        Assert.Contains("value=\"实验中学\"", config);
    }

    internal static async Task<Guid> PluginCredentialIdAsync(TestWebApplicationFactory factory)
    {
        await factory.GetPluginTokenAsync();
        using var scope = factory.Services.CreateScope();
        // SQLite 不能按 DateTimeOffset 排序，取回后在内存中选最新配对的凭据。
        var credentials = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PluginCredentials.AsNoTracking().ToListAsync();
        return credentials.OrderByDescending(x => x.CreatedAt).First().Id;
    }

    private static async Task<HttpClient> LoggedInBrowserAsync(TestWebApplicationFactory factory)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        return browser;
    }
}
