using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
