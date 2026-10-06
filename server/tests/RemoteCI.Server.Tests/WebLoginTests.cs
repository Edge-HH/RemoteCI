using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>客户端一键打开 WebUI：设备会话换取一次性网页登录票据，浏览器打开 /WebLogin 后自动登录。</summary>
public sealed class WebLoginTests
{
    [Fact]
    public async Task WebTicket_SignsBrowserInOnceAndRedirectsToLocalReturnUrl()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsync("/api/auth/web-ticket", null)).StatusCode);
        var issued = await api.PostAsync("/api/auth/web-ticket", null);
        issued.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var path = json.RootElement.GetProperty("path").GetString()!;
        Assert.StartsWith("/WebLogin?t=", path);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var redeem = await browser.GetAsync(path + "&returnUrl=%2FControl");
        Assert.Equal(HttpStatusCode.Redirect, redeem.StatusCode);
        Assert.Equal("/Control", redeem.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Users")).StatusCode);

        // 票据一次性使用：新的浏览器再次打开同一链接会回到登录页。
        using var other = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var reused = await other.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, reused.StatusCode);
        Assert.StartsWith("/Login", reused.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task WebTicket_IgnoresExternalReturnUrl()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        var body = await api.PostAsync("/api/auth/web-ticket", null);
        var path = (await body.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var redeem = await browser.GetAsync(path + "&returnUrl=https%3A%2F%2Fevil.example%2F");
        Assert.Equal(HttpStatusCode.Redirect, redeem.StatusCode);
        Assert.DoesNotContain("evil.example", redeem.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task WebTicket_IsStoredOnlyAsHash()
    {
        await using var factory = new TestWebApplicationFactory();
        var (ticket, userId) = await IssueForAdminAsync(factory);

        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebLoginTickets.SingleAsync(x => x.UserId == userId);
        Assert.NotEqual(ticket, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
    }

    [Fact]
    public async Task WebTicket_ExpiredTicketFails()
    {
        await using var factory = new TestWebApplicationFactory();
        var (ticket, userId) = await IssueForAdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebLoginTickets
                .Where(x => x.UserId == userId)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.ExpiresAtUnixMs, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()));
        }

        Assert.Null(await RedeemAsync(factory, ticket));
    }

    [Fact]
    public async Task WebTicket_ConcurrentRedeemSucceedsExactlyOnce()
    {
        await using var factory = new TestWebApplicationFactory();
        var (ticket, _) = await IssueForAdminAsync(factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RedeemAsync(factory, ticket)));
        Assert.Single(results, x => x is not null);
    }

    [Fact]
    public async Task WebTicket_NewTicketRevokesPreviousOne()
    {
        await using var factory = new TestWebApplicationFactory();
        var (first, _) = await IssueForAdminAsync(factory);
        var (second, _) = await IssueForAdminAsync(factory);

        Assert.Null(await RedeemAsync(factory, first));
        Assert.NotNull(await RedeemAsync(factory, second));
    }

    [Fact]
    public async Task WebTicket_FailsAfterAccountDisabled()
    {
        await using var factory = new TestWebApplicationFactory();
        var (ticket, userId) = await IssueForAdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(userId.ToString()))!;
            user.Enabled = false;
            await users.UpdateAsync(user);
        }

        Assert.Null(await RedeemAsync(factory, ticket));
    }

    [Fact]
    public async Task WebTicket_FailsAfterPasswordChangeOrSecurityStampRotation()
    {
        await using var factory = new TestWebApplicationFactory();
        var (passwordTicket, userId) = await IssueForAdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(userId.ToString()))!;
            Assert.True((await users.ChangePasswordAsync(
                user, TestWebApplicationFactory.AdminPassword, "Changed-Admin-Password-2026")).Succeeded);
        }
        Assert.Null(await RedeemAsync(factory, passwordTicket));

        var (stampTicket, _) = await IssueForAdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(userId.ToString()))!);
        }
        Assert.Null(await RedeemAsync(factory, stampTicket));
    }

    [Fact]
    public async Task WebTicket_SurvivesRestartAndRedeemsOnceAcrossInstances()
    {
        // 两个服务实例共享同一数据库：A 签发的票据在 B 上可兑换（服务重启同理），且之后 A 也无法再次兑换。
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        await using var first = TestWebApplicationFactory.ForDatabase(databasePath);
        var (ticket, _) = await IssueForAdminAsync(first);
        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);

        Assert.NotNull(await RedeemAsync(second, ticket));
        Assert.Null(await RedeemAsync(first, ticket));
    }

    private static async Task<(string Ticket, Guid UserId)> IssueForAdminAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = (await users.FindByNameAsync(TestWebApplicationFactory.AdminUsername))!;
        var issued = await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().CreateWebLoginTicketAsync(admin.Id);
        return (issued.Ticket, admin.Id);
    }

    private static async Task<AppUser?> RedeemAsync(TestWebApplicationFactory factory, string ticket)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().RedeemWebLoginTicketAsync(ticket);
    }
}
