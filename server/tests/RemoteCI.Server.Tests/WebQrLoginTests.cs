using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;
using static RemoteCI.Server.Tests.TestWebApplicationFactory;

namespace RemoteCI.Server.Tests;

/// <summary>网页扫码登录：浏览器显示二维码，已登录的手机 App 扫码确认后由该浏览器登录。</summary>
public sealed class WebQrLoginTests
{
    [Fact]
    public async Task PhoneScanAndConfirm_SignsInTheBrowserThatShowedTheCode()
    {
        await using var factory = new TestWebApplicationFactory();
        var phone = await factory.LoginAsync();
        using var api = factory.CreateClient();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var csrf = Csrf(await browser.GetStringAsync("/Login"));

        var created = await Form(browser, "/Login?handler=QrCreate", csrf, []);
        var code = created.GetProperty("code").GetString()!;
        var pollToken = created.GetProperty("pollToken").GetString()!;
        Assert.Contains("<svg", created.GetProperty("svg").GetString());
        Assert.Equal("pending", (await Form(browser, "/Login?handler=QrStatus", csrf, Poll(code, pollToken))).GetProperty("state").GetString());

        var scanned = await api.SendAsync(Bearer(HttpMethod.Post, "/api/auth/web-qr/scan", phone.AccessToken, new WebQrLoginScanRequest { Code = code }));
        Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await scanned.Content.ReadFromJsonAsync<WebQrLoginScanResponse>())!.Browser));
        var status = await Form(browser, "/Login?handler=QrStatus", csrf, Poll(code, pollToken));
        Assert.Equal("scanned", status.GetProperty("state").GetString());
        Assert.Equal(phone.User.DisplayName, status.GetProperty("scannedBy").GetString());

        // 不知道轮询令牌的人即使拿到二维码也只能看到“已过期”，更不能完成登录。
        Assert.Equal("expired", (await Form(browser, "/Login?handler=QrStatus", csrf, Poll(code, "forged"))).GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(Bearer(HttpMethod.Post, "/api/auth/web-qr/confirm", phone.AccessToken,
            new WebQrLoginConfirmRequest { Code = code, Approve = true }))).StatusCode);
        Assert.Equal("approved", (await Form(browser, "/Login?handler=QrStatus", csrf, Poll(code, pollToken))).GetProperty("state").GetString());

        var completed = await browser.PostAsync("/Login?handler=QrComplete", FormContent(csrf, new Dictionary<string, string>
        {
            ["code"] = code, ["pollToken"] = pollToken, ["rememberMe"] = "false",
        }));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        // 与账号密码登录相同的落地页：单班级账号进入总览（Index 页路由为根路径）。
        Assert.Equal("/", (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("redirectUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Index")).StatusCode);

        // 挑战只能兑换一次：即使另一个浏览器拿到了挑战码和轮询令牌也无法再登录。
        using var other = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var otherCsrf = Csrf(await other.GetStringAsync("/Login"));
        var replay = await other.PostAsync("/Login?handler=QrComplete", FormContent(otherCsrf, new Dictionary<string, string>
        {
            ["code"] = code, ["pollToken"] = pollToken,
        }));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task OnlyTheScanningDeviceSessionCanConfirm()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var api = factory.CreateClient();
        (await api.SendAsync(Bearer(HttpMethod.Post, "/api/users", owner.AccessToken, new CreateUserRequest
        {
            Username = "qr.other", DisplayName = "另一位老师", Password = "Qr-Other-Password-2026", ClassId = DefaultClassId,
        }))).EnsureSuccessStatusCode();
        var other = await factory.LoginAsync("qr.other", "Qr-Other-Password-2026");

        var qr = factory.Services.GetService(typeof(WebQrLoginService)) as WebQrLoginService;
        var (code, _, _) = qr!.Create("Mozilla/5.0 (Windows NT 10.0) Chrome/130.0", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(Bearer(HttpMethod.Post, "/api/auth/web-qr/scan", owner.AccessToken,
            new WebQrLoginScanRequest { Code = code }))).StatusCode);
        // 已被扫码后，其他账号既不能再扫，也不能替扫码人确认。
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(Bearer(HttpMethod.Post, "/api/auth/web-qr/scan", other.AccessToken,
            new WebQrLoginScanRequest { Code = code }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(Bearer(HttpMethod.Post, "/api/auth/web-qr/confirm", other.AccessToken,
            new WebQrLoginConfirmRequest { Code = code, Approve = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsJsonAsync("/api/auth/web-qr/scan", new WebQrLoginScanRequest { Code = code })).StatusCode);
        Assert.Equal("Windows · Chrome", WebQrLoginService.DescribeBrowser("Mozilla/5.0 (Windows NT 10.0) Chrome/130.0"));
    }

    private static Dictionary<string, string> Poll(string code, string pollToken) => new() { ["code"] = code, ["pollToken"] = pollToken };

    private static async Task<JsonElement> Form(HttpClient browser, string url, string csrf, Dictionary<string, string> data)
    {
        var response = await browser.PostAsync(url, FormContent(csrf, data));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static FormUrlEncodedContent FormContent(string csrf, Dictionary<string, string> data)
    {
        data["__RequestVerificationToken"] = csrf;
        return new FormUrlEncodedContent(data);
    }

    private static string Csrf(string html)
    {
        var match = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
