using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
            new AppGuardPolicy
            {
                Enabled = true, RestartAfterExit = true, RestartAfterExitDelayMinutes = 40, BackupIntervalHours = 0,
                EnhancedMode = true, RandomBackupPath = true, BackupDirectoryName = "Campus.Backup", ProcessName = "Campus.Agent",
                BackupPaths = [@"D:\Backups\ClassIsland"],
            }));
        put.EnsureSuccessStatusCode();
        var global = (await put.Content.ReadFromJsonAsync<AppGuardPolicy>())!;
        Assert.True(global.Enabled);
        Assert.True(global.EnhancedMode);
        Assert.True(global.RandomBackupPath);
        Assert.Equal("Campus.Backup", global.BackupDirectoryName);
        Assert.Equal("Campus.Agent", global.ProcessName);
        Assert.Equal([@"D:\Backups\ClassIsland"], global.BackupPaths);
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

    [Fact]
    public async Task GuardOnlyControlPage_ShowsOnlyRemoteLaunch()
    {
        await using var factory = new TestWebApplicationFactory();
        var token = await factory.GetPluginTokenAsync();
        using var guard = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?token={Uri.EscapeDataString(token)}&client=guard"), CancellationToken.None);
        var capabilities = JsonSerializer.SerializeToUtf8Bytes(
            Envelope.PeerCapabilities(new PeerCapabilities
            {
                Capabilities = [RemoteCiCapabilities.AppGuard, RemoteCiCapabilities.AppGuardLaunch],
            }), JsonDefaults.Options);
        await guard.SendAsync(capabilities, WebSocketMessageType.Text, true, CancellationToken.None);
        var registry = factory.Services.GetRequiredService<PeerRegistry>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!registry.HasGuardFor(TestWebApplicationFactory.DefaultClassId)) await Task.Delay(20, timeout.Token);
        using var browser = await LoggedInBrowserAsync(factory);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));

        Assert.Contains("远程打开 ClassIsland", html);
        Assert.DoesNotContain("音量调节", html);
        Assert.DoesNotContain("屏幕截图", html);
        Assert.DoesNotContain("班级设置", html);
        Assert.DoesNotContain("插件扩展", html);
        Assert.DoesNotContain("隐藏主界面", html);
        Assert.DoesNotContain("显示主界面", html);
        var guardPage = WebUtility.HtmlDecode(await browser.GetStringAsync("/AppGuard"));
        Assert.Contains("守护版本过旧，课表插件上线后自动更新", guardPage);
        Assert.DoesNotContain("关闭守护", guardPage);
        Assert.DoesNotContain("卸载守护", guardPage);
        await guard.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test finished", CancellationToken.None);
    }

    [Fact]
    public async Task GuardOnlyAdminPage_ClosesAndUninstallsGuard_AndPersistsDevicePolicy()
    {
        await using var factory = new TestWebApplicationFactory();
        var credentialId = await PluginCredentialIdAsync(factory);
        using var guard = await ConnectGuardAsync(factory,
            RemoteCiCapabilities.AppGuard, RemoteCiCapabilities.AppGuardLaunch, RemoteCiCapabilities.AppGuardManage);
        var registry = factory.Services.GetRequiredService<PeerRegistry>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!registry.GuardForCredentialSupports(credentialId, RemoteCiCapabilities.AppGuardManage))
                await Task.Delay(20, timeout.Token);
        using var browser = await LoggedInBrowserAsync(factory);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/AppGuard"));
        Assert.Contains("守护在线 · 课表离线", html);
        Assert.Contains("关闭守护", html);
        Assert.Contains("卸载守护", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        });

        var closeTask = browser.PostAsync($"/AppGuard?handler=CloseGuard&credentialId={credentialId}", form);
        var closeEnvelope = await ReceiveEnvelopeAsync(guard, Protocol.MessageTypeCommand);
        var closeCommand = JsonSerializer.Deserialize<CommandMessage>(
            JsonSerializer.Serialize(closeEnvelope.Payload), JsonDefaults.Options)!;
        Assert.Equal(CommandKind.StopAppGuard, closeCommand.Command);
        Assert.Equal(UserRole.Admin, closeCommand.RequestedBy!.Role);
        await SendResultAsync(guard, closeEnvelope.MessageId);
        var close = await closeTask;
        Assert.Equal(HttpStatusCode.Redirect, close.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var view = await scope.ServiceProvider.GetRequiredService<AppGuardService>().GetDeviceAsync(credentialId);
            Assert.True(view.Overridden);
            Assert.False(view.Policy.Enabled);
        }

        var uninstallTask = browser.PostAsync($"/AppGuard?handler=UninstallGuard&credentialId={credentialId}", form);
        var uninstallEnvelope = await ReceiveEnvelopeAsync(guard, Protocol.MessageTypeCommand);
        var uninstallCommand = JsonSerializer.Deserialize<CommandMessage>(
            JsonSerializer.Serialize(uninstallEnvelope.Payload), JsonDefaults.Options)!;
        Assert.Equal(CommandKind.UninstallAppGuard, uninstallCommand.Command);
        await SendResultAsync(guard, uninstallEnvelope.MessageId);
        var uninstall = await uninstallTask;
        Assert.Equal(HttpStatusCode.Redirect, uninstall.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var view = await scope.ServiceProvider.GetRequiredService<AppGuardService>().GetDeviceAsync(credentialId);
            Assert.True(view.Overridden);
            Assert.False(view.Policy.Enabled);
            Assert.False(view.Policy.EnhancedMode);
        }
        await guard.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test finished", CancellationToken.None);
    }

    internal static async Task<Guid> PluginCredentialIdAsync(TestWebApplicationFactory factory)
    {
        // 默认插件的凭据在首次配对时固定下来；其他测试会额外配对设备，不能再用“最新凭据”指代它。
        await factory.GetPluginTokenAsync();
        return factory.PluginCredentialId;
    }

    private static async Task<WebSocket> ConnectGuardAsync(TestWebApplicationFactory factory, params string[] capabilities)
    {
        var token = await factory.GetPluginTokenAsync();
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?token={Uri.EscapeDataString(token)}&client=guard"),
            CancellationToken.None);
        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(
                Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = capabilities }), JsonDefaults.Options),
            WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }

    private static async Task<Envelope> ReceiveEnvelopeAsync(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("守护连接已被服务端关闭");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(
                Encoding.UTF8.GetString(stream.ToArray()), JsonDefaults.Options)!;
            if (envelope.Type == expectedType) return envelope;
        }
    }

    private static async Task SendResultAsync(WebSocket socket, string messageId) =>
        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new Envelope
            {
                Type = Protocol.MessageTypeCommandResult,
                ReplyToMessageId = messageId,
                Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok },
            }, JsonDefaults.Options),
            WebSocketMessageType.Text, true, CancellationToken.None);

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
