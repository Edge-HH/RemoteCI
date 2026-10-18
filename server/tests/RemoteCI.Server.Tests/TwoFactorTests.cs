using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class TwoFactorTests
{
    // RFC 6238 Appendix B, SHA-1 vectors (independent known answers).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_MatchesRfc6238(long seconds, string expected) =>
        Assert.Equal(expected, TwoFactorService.CodeAt("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", seconds / 30, 8));

    [Fact]
    public async Task Binding_RequiresPasswordAndProof_AndShowsRecoveryCodesOnlyOnce()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = Browser(factory);
        Assert.Equal(HttpStatusCode.SeeOther, (await PasswordLogin(browser)).StatusCode);
        var denied = await Post(browser, "/TwoFactor", "Begin", new() { ["CurrentPassword"] = "wrong-password" });
        Assert.DoesNotContain("totp-qr", await denied.Content.ReadAsStringAsync());
        var begun = await Post(browser, "/TwoFactor", "Begin", new() { ["CurrentPassword"] = TestWebApplicationFactory.AdminPassword });
        var html = await begun.Content.ReadAsStringAsync();
        var secret = Regex.Match(html, "class=\"totp-key\">([A-Z2-7]+)").Groups[1].Value;
        var setup = Input(html, "SetupToken");
        Assert.Equal(32, secret.Length);
        Assert.NotEmpty(setup);
        using (var scope = factory.Services.CreateScope())
            Assert.False((await Admin(scope)).TwoFactorEnabled);
        var bad = await Post(browser, "/TwoFactor", "Enable", new() { ["SetupToken"] = setup, ["Code"] = "invalid" });
        Assert.Contains("验证码错误", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        var enabled = await Post(browser, "/TwoFactor", "Enable", new()
        {
            ["SetupToken"] = setup,
            ["Code"] = TwoFactorService.CodeAt(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30),
        });
        html = await enabled.Content.ReadAsStringAsync();
        var codes = Regex.Matches(html, "[A-F0-9]{4}(?:-[A-F0-9]{4}){3}");
        Assert.Equal(10, codes.Count);
        var later = await browser.GetStringAsync("/TwoFactor");
        Assert.DoesNotContain(codes[0].Value, later);
        using var after = factory.Services.CreateScope();
        var user = await Admin(after);
        Assert.True(user.TwoFactorEnabled);
        Assert.NotEqual(secret, user.ProtectedTotpKey);
        Assert.DoesNotContain(codes[0].Value.Replace("-", ""), user.TwoFactorRecoveryHashes!);
        Assert.False(after.ServiceProvider.GetRequiredService<AppDbContext>().Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task PasswordLogin_DoesNotAuthenticateUntilCode_AndPreservesRememberMe()
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        using var browser = Browser(factory);
        var first = await PasswordLogin(browser, remember: true);
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal("/LoginSecondFactor", first.Headers.Location?.OriginalString);
        Assert.Contains("/Login", (await browser.GetAsync("/Account")).Headers.Location?.OriginalString);
        var wrong = await Post(browser, "/LoginSecondFactor", null, new() { ["Code"] = "invalid" });
        Assert.Contains("验证码错误", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        var done = await Post(browser, "/LoginSecondFactor", null, new() { ["Code"] = enabled.Codes[0], ["UseRecoveryCode"] = "true" });
        Assert.Equal(HttpStatusCode.SeeOther, done.StatusCode);
        Assert.Contains(done.Headers.GetValues("Set-Cookie"), x => x.StartsWith("RemoteCI.Web=") && x.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
        using var other = Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/LoginSecondFactor")).StatusCode);
    }

    [Fact]
    public async Task ApiLogin_RequiresCode_RejectsReplay_AndDoesNotResetFailuresAfterPassword()
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        var required = await factory.LoginAsync();
        Assert.True(required.TwoFactorRequired);
        Assert.Empty(required.AccessToken);
        Assert.Empty(required.DeviceSecret);
        using var client = factory.CreateClient();
        var code = TwoFactorService.CodeAt(enabled.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1);
        var request = Login(code);
        var success = await client.PostAsJsonAsync("/api/auth/login", request);
        success.EnsureSuccessStatusCode();
        Assert.NotEmpty((await success.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
        for (var i = 0; i < 7; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", Login("invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", Login(enabled.Codes[0], true))).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().IsLockedOutAsync(await Admin(scope)));
    }

    [Fact]
    public async Task RecoveryCode_IsConsumedExactlyOnceAcrossConcurrentRequests()
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<TwoFactorService>().VerifyAsync(await Admin(scope), enabled.Codes[0], true, default);
        }));
        Assert.Single(attempts, x => x);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingLogin_FailsAfterSecurityStampChangeOrAccountDisabled(bool stamp)
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        using var browser = Browser(factory);
        await PasswordLogin(browser);
        var csrf = Input(await browser.GetStringAsync("/LoginSecondFactor"), "__RequestVerificationToken");
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await Admin(scope);
            if (stamp) await users.UpdateSecurityStampAsync(user);
            else { user.Enabled = false; await users.UpdateAsync(user); }
        }
        var result = await browser.PostAsync("/LoginSecondFactor", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Code"] = enabled.Codes[0], ["UseRecoveryCode"] = "true", ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.StartsWith("/Login", result.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task WebTicketAndMobileTicket_CannotBypassTwoFactor()
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        using var scope = factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var user = await Admin(scope);
        var web = await identities.CreateWebLoginTicketAsync(user.Id);
        using var browser = Browser(factory);
        var html = await browser.GetStringAsync("/WebLogin?t=" + web.Ticket);
        Assert.Contains("/LoginSecondFactor", html);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
        var mobile = await identities.CreateMobileLoginTicketAsync(user.Id);
        var required = await identities.RedeemMobileLoginTicketAsync(new() { Ticket = mobile.Ticket });
        Assert.True(required.TwoFactorRequired);
        Assert.Empty(required.AccessToken);
        var auth = await identities.RedeemMobileLoginTicketAsync(new() { Ticket = mobile.Ticket, TwoFactorCode = enabled.Codes[0], UseRecoveryCode = true });
        Assert.NotEmpty(auth.AccessToken);
        await Assert.ThrowsAsync<IdentityOperationException>(() => identities.RedeemMobileLoginTicketAsync(new() { Ticket = mobile.Ticket }));
    }

    [Fact]
    public async Task ApprovedWebQr_StillRequiresSecondFactor_AndCancellationClearsChallenge()
    {
        await using var factory = new TestWebApplicationFactory();
        await EnableAdmin(factory);
        using var scope = factory.Services.CreateScope();
        var user = await Admin(scope);
        var qr = scope.ServiceProvider.GetRequiredService<WebQrLoginService>();
        var (code, poll, _) = qr.Create("test browser", "127.0.0.1");
        Assert.NotNull(qr.Scan(code, user.Id, user.DisplayName));
        Assert.True(qr.Decide(code, user.Id, true));
        using var browser = Browser(factory);
        var completed = await Post(browser, "/Login", "QrComplete", new() { ["code"] = code, ["pollToken"] = poll });
        completed.EnsureSuccessStatusCode();
        var response = await completed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("/LoginSecondFactor", response.GetProperty("redirectUrl").GetString());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
        await Post(browser, "/LoginSecondFactor", "Cancel", new());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/LoginSecondFactor")).StatusCode);
    }

    [Fact]
    public async Task Disable_RequiresPasswordAndCode_RevokesSessions_AndAllowsPasswordLogin()
    {
        await using var factory = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(factory);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var auth = await identities.LoginAsync(Login(enabled.Codes[0], true));
        var user = await Admin(scope);
        await Assert.ThrowsAsync<IdentityOperationException>(() => service.DisableAsync(user, "wrong-password", enabled.Codes[1], true, default));
        await service.DisableAsync(user, TestWebApplicationFactory.AdminPassword, enabled.Codes[1], true, default);
        using var verify = factory.Services.CreateScope();
        Assert.Null(await verify.ServiceProvider.GetRequiredService<IdentityCoordinator>().ValidateAccessTokenAsync(auth.AccessToken));
        Assert.Null(user.ProtectedTotpKey);
        Assert.Null(user.TwoFactorRecoveryHashes);
        Assert.NotEmpty((await factory.LoginAsync()).AccessToken);
    }

    [Fact]
    public async Task EncryptedBackup_RestoresTwoFactorOnAnotherServer()
    {
        await using var first = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(first);
        byte[] exported;
        using (var scope = first.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
            var local = await archive.CaptureAsync();
            Assert.True(local.Users.Single().TwoFactorEnabled);
            Assert.Null(local.Users.Single().TotpSecret);
            Assert.NotEqual(enabled.Secret, local.Users.Single().ProtectedTotpKey);
            exported = await archive.ExportEncryptedAsync("Backup-Password-2026");
        }
        await using var second = new TestWebApplicationFactory();
        using (var scope = second.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
            await archive.ApplyAsync(archive.ReadEncrypted(exported, "Backup-Password-2026"));
            var user = await Admin(scope);
            Assert.True(user.TwoFactorEnabled);
            Assert.Equal(enabled.Secret, scope.ServiceProvider.GetRequiredService<TwoFactorService>().UnprotectKey(user));
        }
        Assert.True((await second.LoginAsync()).TwoFactorRequired);
    }

    [Fact]
    public async Task EncryptedBackup_KeepsCiphertext_WhenKeyRingCannotDecrypt()
    {
        await using var first = new TestWebApplicationFactory();
        var enabled = await EnableAdmin(first);
        byte[] exported;
        string foreign;
        using (var scope = first.Services.CreateScope())
        {
            var user = await Admin(scope);
            // 模拟 keys/ 丢失：密文来自另一套密钥环，当前服务器找不到对应密钥。
            foreign = new EphemeralDataProtectionProvider()
                .CreateProtector("RemoteCI.Totp.Key.v1", user.Id.ToString()).Protect(enabled.Secret);
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(x => x.Id == user.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.ProtectedTotpKey, foreign));
            var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
            Assert.Equal(TestWebApplicationFactory.AdminUsername, Assert.Single(await archive.FindUnreadableTwoFactorUsersAsync()));
            exported = await archive.ExportEncryptedAsync("Backup-Password-2026");
        }
        await using var second = new TestWebApplicationFactory();
        using (var scope = second.Services.CreateScope())
        {
            var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
            var snapshot = archive.ReadEncrypted(exported, "Backup-Password-2026");
            Assert.Null(snapshot.Users.Single().TotpSecret);
            await archive.ApplyAsync(snapshot);
            var user = await Admin(scope);
            Assert.True(user.TwoFactorEnabled);
            Assert.Equal(foreign, user.ProtectedTotpKey);
        }
        Assert.True((await second.LoginAsync()).TwoFactorRequired);
        using var client = second.CreateClient();
        var recovered = await client.PostAsJsonAsync("/api/auth/login", Login(enabled.Codes[0], true));
        recovered.EnsureSuccessStatusCode();
        Assert.NotEmpty((await recovered.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken);
    }

    private static LoginRequest Login(string code, bool recovery = false) => new()
    {
        Username = TestWebApplicationFactory.AdminUsername, Password = TestWebApplicationFactory.AdminPassword,
        TwoFactorCode = code, UseRecoveryCode = recovery, DeviceName = "2FA test",
    };

    private static async Task<(string Secret, string[] Codes)> EnableAdmin(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var user = await Admin(scope);
        var twoFactor = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var token = twoFactor.BeginSetup(user);
        var setup = twoFactor.ReadSetup(user, token);
        var codes = await twoFactor.EnableAsync(user, token, TwoFactorService.CodeAt(setup.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), default);
        return (setup.Secret, codes);
    }

    private static Task<AppUser> Admin(IServiceScope scope) => FindAdmin(scope);
    private static async Task<AppUser> FindAdmin(IServiceScope scope) =>
        (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(TestWebApplicationFactory.AdminUsername))!;

    private static HttpClient Browser(TestWebApplicationFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    private static Task<HttpResponseMessage> PasswordLogin(HttpClient browser, bool remember = false) =>
        Post(browser, "/Login", null, new()
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername, ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["Input.RememberMe"] = remember.ToString(),
        });

    private static async Task<HttpResponseMessage> Post(HttpClient client, string page, string? handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(page);
        fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
        return await client.PostAsync(page + (handler is null ? "" : "?handler=" + handler), new FormUrlEncodedContent(fields));
    }

    private static string Input(string html, string name) => WebUtility.HtmlDecode(
        Regex.Match(html, "name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
}
