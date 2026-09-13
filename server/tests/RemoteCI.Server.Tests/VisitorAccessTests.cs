using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Pages;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class VisitorAccessTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "/", true)]
    [InlineData(null, "/Index", true)]
    [InlineData(null, "/Index?x=1", true)]
    [InlineData("visitor", null, false)]
    [InlineData("visitor", "/", false)]
    [InlineData(null, "/Users", false)]
    [InlineData(null, "/Schedule", false)]
    public void LoginLanding_OnlyAutoEntersWebUiHome(string? from, string? returnUrl, bool expected) =>
        Assert.Equal(expected, LoginModel.ShouldAutoEnter(from, returnUrl));

    [Fact]
    public async Task VisitorPage_IsHiddenUntilEnabledAndOnlyShowsSchedule()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);

        var loginHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Login"));
        Assert.DoesNotContain("仅查看", loginHtml);
        var disabled = await browser.GetAsync("/Visitor");
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.Equal("/Login", disabled.Headers.Location?.OriginalString);

        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var usersHtml = await browser.GetStringAsync("/Users");
        Assert.Contains("启用访客功能", usersHtml);
        Assert.Contains("自动进入访客页", usersHtml);
        Assert.Contains("disabled", Regex.Match(usersHtml, "data-visitor-auto-enter[^>]*>").Value);

        var saved = await PostRazorFormAsync(browser, "/Users?handler=VisitorAccess", usersHtml, new Dictionary<string, string>
        {
            ["VisitorAccessEnabled"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using var guest = CreateBrowser(factory);
        var guestLogin = WebUtility.HtmlDecode(await guest.GetStringAsync("/Login"));
        Assert.Contains("仅查看", guestLogin);
        Assert.Contains("class=\"login-guest\"", guestLogin);
        Assert.DoesNotContain("自动进入访客页", guestLogin);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(new ScheduleBundle
            {
                FromDate = "2026-09-13",
                Days =
                [
                    new ScheduleDay
                    {
                        Date = "2026-09-14",
                        Revision = "visitor-1",
                        Enabled = true,
                        Courses =
                        [
                            new CourseEntry
                            {
                                Index = 0,
                                Label = "第一节",
                                Subject = "数学",
                                StartTime = "08:00",
                                EndTime = "08:45",
                                Enabled = true,
                            },
                        ],
                    },
                ],
            });
        }

        var visitor = await guest.GetAsync("/Visitor");
        Assert.Equal(HttpStatusCode.OK, visitor.StatusCode);
        var visitorHtml = WebUtility.HtmlDecode(await visitor.Content.ReadAsStringAsync());
        Assert.Contains("七日课表", visitorHtml);
        Assert.Contains("返回登录", visitorHtml);
        Assert.Contains("from=visitor", visitorHtml);
        Assert.Contains("数学", visitorHtml);
        Assert.Contains("08:00–08:45", visitorHtml);
        Assert.DoesNotContain("立即拉取课表", visitorHtml);
        Assert.DoesNotContain("提交修改", visitorHtml);
        Assert.DoesNotContain("人员权限", visitorHtml);
        Assert.DoesNotContain("class=\"sidebar\"", visitorHtml);
    }

    [Fact]
    public async Task AutoEnter_RequiresVisitorAccessAndCanReturnToLogin()
    {
        await using var factory = new TestWebApplicationFactory();
        using var admin = CreateBrowser(factory);
        await LoginWebUiAsync(admin, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var usersHtml = await admin.GetStringAsync("/Users");
        var rejected = await PostRazorFormAsync(admin, "/Users?handler=VisitorAccess", usersHtml, new Dictionary<string, string>
        {
            ["AutoEnterVisitorPage"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>().GetAsync();
            Assert.False(state.Enabled);
            Assert.False(state.AutoEnter);
        }

        usersHtml = await admin.GetStringAsync("/Users");
        var enabled = await PostRazorFormAsync(admin, "/Users?handler=VisitorAccess", usersHtml, new Dictionary<string, string>
        {
            ["VisitorAccessEnabled"] = "true",
            ["AutoEnterVisitorPage"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);

        using var guest = CreateBrowser(factory);
        var landing = await guest.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.Equal("/Visitor", landing.Headers.Location?.OriginalString);
        var home = await guest.GetAsync("/Login?ReturnUrl=%2F");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Visitor", home.Headers.Location?.OriginalString);
        var protectedPage = await guest.GetAsync("/Login?ReturnUrl=%2FUsers");
        Assert.Equal(HttpStatusCode.OK, protectedPage.StatusCode);
        var stay = await guest.GetAsync("/Login?from=visitor");
        Assert.Equal(HttpStatusCode.OK, stay.StatusCode);
        var stayHtml = WebUtility.HtmlDecode(await stay.Content.ReadAsStringAsync());
        Assert.Contains("仅查看", stayHtml);
        Assert.Contains("登录你的账号", stayHtml);
    }

    [Fact]
    public async Task ConfigurationBackup_PreservesVisitorAccessSettings()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>()
                .SetAsync(true, true);
            var snapshot = await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CaptureAsync();
            Assert.True(snapshot.Metadata.VisitorAccessEnabled);
            Assert.True(snapshot.Metadata.AutoEnterVisitorPage);
            await scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>().SetAsync(false, false);
            await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().ApplyAsync(snapshot);
        }

        using var verify = factory.Services.CreateScope();
        var restored = await verify.ServiceProvider.GetRequiredService<VisitorAccessSettings>().GetAsync();
        Assert.True(restored.Enabled);
        Assert.True(restored.AutoEnter);

        var legacy = JsonSerializer.Deserialize<MetadataSnapshot>(
            """{"accountVersion":3,"forceSenderInTitle":true,"schedulePullIntervalMinutes":15}""",
            JsonDefaults.Options);
        Assert.NotNull(legacy);
        Assert.False(legacy.VisitorAccessEnabled);
        Assert.False(legacy.AutoEnterVisitorPage);
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
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
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
