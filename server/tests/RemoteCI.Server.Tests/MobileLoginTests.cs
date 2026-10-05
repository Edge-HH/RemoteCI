using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>手机扫码登录：系统配置可指定二维码服务器地址，二维码票据一次性兑换为当前 WebUI 账号的设备会话。</summary>
public sealed class MobileLoginTests
{
    [Fact]
    public async Task MobileLoginTicket_NewTicketRevokesPreviousOne()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var adminId = (await identities.ListUsersAsync()).Single(x => x.Username == TestWebApplicationFactory.AdminUsername).Id;
        var stale = await identities.CreateMobileLoginTicketAsync(adminId);
        var fresh = await identities.CreateMobileLoginTicketAsync(adminId);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/mobile-login", new MobileLoginRequest { Ticket = stale.Ticket })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/mobile-login", new MobileLoginRequest { Ticket = fresh.Ticket })).StatusCode);
    }

    [Fact]
    public async Task SystemConfig_MobileServerUrlDrivesLoginQr()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginWebUiAsync(browser);

        var configHtml = await browser.GetStringAsync("/SystemConfig");
        Assert.Contains("name=\"MobileServerUrl\"", configHtml);
        var invalid = await PostFormAsync(browser, "/SystemConfig?handler=SaveMobileLoginSettings", configHtml,
            new() { ["MobileServerUrl"] = "ftp://example.com" });
        Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MobileLoginSettings>().GetServerUrlAsync());

        var saved = await PostFormAsync(browser, "/SystemConfig?handler=SaveMobileLoginSettings", configHtml,
            new() { ["MobileServerUrl"] = " https://ci.example.com/remoteci/ " });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal("https://ci.example.com/remoteci",
                await scope.ServiceProvider.GetRequiredService<MobileLoginSettings>().GetServerUrlAsync());

        var indexHtml = await browser.GetStringAsync("/");
        Assert.Contains("https://ci.example.com/remoteci", indexHtml);
        var qr = await PostFormAsync(browser, "/?handler=MobileLoginQr", indexHtml, new());
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        using var json = JsonDocument.Parse(await qr.Content.ReadAsStringAsync());
        Assert.Equal("https://ci.example.com/remoteci", json.RootElement.GetProperty("serverUrl").GetString());
        Assert.StartsWith("<svg", json.RootElement.GetProperty("svg").GetString());
    }

    [Fact]
    public void LoginQrPayload_EncodesServerUserAndTicket()
    {
        var payload = MobileLoginSettings.BuildLoginQrPayload("https://ci.example.com/a b", "张三", "abc123");
        Assert.Equal("remoteci://login?server=https%3A%2F%2Fci.example.com%2Fa%20b&user=%E5%BC%A0%E4%B8%89&ticket=abc123", payload);
        Assert.Null(MobileLoginSettings.Normalize("   "));
        Assert.Throws<ArgumentException>(() => MobileLoginSettings.Normalize("https://ci.example.com/?x=1"));
    }

    private static async Task LoginWebUiAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var response = await PostFormAsync(browser, "/Login", html, new()
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient browser, string path, string html, Dictionary<string, string> values)
    {
        var match = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "表单页必须包含 CSRF 令牌");
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        return await browser.PostAsync(path, new FormUrlEncodedContent(values));
    }
}
