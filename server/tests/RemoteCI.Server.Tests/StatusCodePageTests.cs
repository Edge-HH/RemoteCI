using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>非法地址要落到 WebUI 状态页并提供返回入口；API 的 404 保持非 HTML。</summary>
public sealed class StatusCodePageTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StatusCodePageTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UnknownPage_RendersNotFoundPageWithLoginLinkForAnonymous()
    {
        using var browser = CreateBrowser();
        var response = await browser.GetAsync("/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("页面不存在", html);
        Assert.Contains("href=\"/Login\"", html);
    }

    [Fact]
    public async Task UnknownPage_LinksAdminBackToOverview()
    {
        using var browser = CreateBrowser();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var response = await browser.GetAsync("/missing/deep/path");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("返回主页", html);
        Assert.Contains("href=\"/\"", html);
    }

    [Fact]
    public async Task UnknownApi_KeepsPlainNotFound()
    {
        using var browser = CreateBrowser();
        var response = await browser.GetAsync("/api/no-such-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("页面不存在", await response.Content.ReadAsStringAsync());
    }

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

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
}
