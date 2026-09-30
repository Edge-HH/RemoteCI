using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>登录页设置：主题、背景不透明度与登录卡片位置需要落到数据库并影响未登录页面的渲染。</summary>
public sealed class LoginSettingsTests
{
    [Fact]
    public async Task LoginSettings_PersistsAppearanceAndRendersCardPosition()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);

        // 默认未设置时，登录页卡片居中。
        using (var anonymous = CreateBrowser(factory))
        {
            var defaultLogin = WebUtility.HtmlDecode(await anonymous.GetStringAsync("/Login"));
            Assert.Contains("--login-card-align:center", defaultLogin);
        }

        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var settingsHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/LoginSettings"));
        Assert.Contains("name=\"Appearance.CardPosition\"", settingsHtml);
        Assert.Contains("data-login-preview-stage", settingsHtml);

        var saved = await PostRazorFormAsync(browser, "/LoginSettings?handler=Save", settingsHtml, new Dictionary<string, string>
        {
            ["Appearance.Theme"] = "Dark",
            ["Appearance.Opacity"] = "40",
            ["Appearance.CardPosition"] = "Right",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var appearance = await scope.ServiceProvider.GetRequiredService<LoginPageSettings>().GetAppearanceAsync();
            Assert.Equal(LoginTheme.Dark, appearance.Theme);
            Assert.Equal(40, appearance.Opacity);
            Assert.Equal(LoginCardPosition.Right, appearance.CardPosition);
        }

        // 设置页预览与登录页都按保存值渲染：预览带不透明度，登录页带卡片位置。
        var reloaded = WebUtility.HtmlDecode(await browser.GetStringAsync("/LoginSettings"));
        Assert.Contains("--login-preview-opacity:0.4", reloaded);
        Assert.Contains("--login-preview-align:end", reloaded);

        using (var anonymous = CreateBrowser(factory))
        {
            var loginHtml = WebUtility.HtmlDecode(await anonymous.GetStringAsync("/Login"));
            Assert.Contains("--login-card-align:end", loginHtml);
            Assert.Contains("data-login-theme-forced=\"dark\"", loginHtml);
        }
    }

    private static HttpClient CreateBrowser(TestWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
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

    private static async Task<HttpResponseMessage> PostRazorFormAsync(
        HttpClient browser,
        string path,
        string html,
        IReadOnlyDictionary<string, string> values)
    {
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Razor 表单页必须包含 CSRF 令牌");
        var fields = new Dictionary<string, string>(values)
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        };
        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}