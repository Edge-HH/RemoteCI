using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
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
        Assert.Contains("自动进入访客页", usersHtml);

        // 访客开关按班级配置：在班级管理页开启默认班级的访客功能。
        var classesHtml = await browser.GetStringAsync("/Classes");
        var saved = await PostRazorFormAsync(browser, "/Classes?handler=ToggleVisitor", classesHtml, new Dictionary<string, string>
        {
            ["id"] = TestWebApplicationFactory.DefaultClassId.ToString(),
            ["enabled"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using var guest = CreateBrowser(factory);
        var guestLogin = WebUtility.HtmlDecode(await guest.GetStringAsync("/Login"));
        Assert.Contains("仅查看", guestLogin);
        Assert.Contains("class=\"login-guest\"", guestLogin);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(TestWebApplicationFactory.DefaultClassId, new ScheduleBundle
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
        Assert.Contains("返回登录", visitorHtml);
        Assert.Contains("from=visitor", visitorHtml);
        Assert.Contains("数学", visitorHtml);
        Assert.Contains("08:00–08:45", visitorHtml);
        Assert.DoesNotContain("立即拉取课表", visitorHtml);
        Assert.DoesNotContain("提交换课", visitorHtml);
        Assert.DoesNotContain("人员权限", visitorHtml);
        Assert.DoesNotContain("class=\"sidebar\"", visitorHtml);
    }

    [Fact]
    public async Task VisitorPage_CanSwitchBetweenVisitorEnabledClasses()
    {
        await using var factory = new TestWebApplicationFactory();
        using var admin = CreateBrowser(factory);
        await LoginWebUiAsync(admin, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        Guid otherClassId;
        using (var scope = factory.Services.CreateScope())
        {
            var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
            otherClassId = (await classrooms.CreateAsync("高二（2）班")).Id;
            await classrooms.SetVisitorAccessAsync(TestWebApplicationFactory.DefaultClassId, true);
            await classrooms.SetVisitorAccessAsync(otherClassId, true);
            // 只给默认班级和“高二（2）班”注入可区分的课表。
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(TestWebApplicationFactory.DefaultClassId, new ScheduleBundle
            {
                FromDate = "2026-09-13",
                Days = [new ScheduleDay { Date = "2026-09-14", Revision = "v-a", Enabled = true, Courses = [new CourseEntry { Index = 0, Subject = "语文", StartTime = "08:00", EndTime = "08:45", Enabled = true }] }],
            });
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(otherClassId, new ScheduleBundle
            {
                FromDate = "2026-09-13",
                Days = [new ScheduleDay { Date = "2026-09-14", Revision = "v-b", Enabled = true, Courses = [new CourseEntry { Index = 0, Subject = "化学", StartTime = "09:00", EndTime = "09:45", Enabled = true }] }],
            });
            // 未开放访客的班级不应出现在访客页。
            await classrooms.CreateAsync("高一（3）班");
        }

        using var guest = CreateBrowser(factory);
        var landing = await guest.GetAsync("/Visitor");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        var landingHtml = WebUtility.HtmlDecode(await landing.Content.ReadAsStringAsync());
        // 默认展示第一个开放班级的课表，且两个开放班级都可选，未开放班级不可见。
        Assert.Contains("语文", landingHtml);
        Assert.Contains("高二（2）班", landingHtml);
        Assert.DoesNotContain("高一（3）班", landingHtml);

        var other = await guest.GetAsync($"/Visitor?class={otherClassId}");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var otherHtml = WebUtility.HtmlDecode(await other.Content.ReadAsStringAsync());
        Assert.Contains("化学", otherHtml);
    }

    [Fact]
    public async Task VisitorPage_FiltersClassesByGroupAndIncludesChildGroups()
    {
        await using var factory = new TestWebApplicationFactory();
        using var admin = CreateBrowser(factory);
        await LoginWebUiAsync(admin, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        Guid rootGroupId;
        using (var scope = factory.Services.CreateScope())
        {
            var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
            var rootGroup = await classrooms.CreateGroupAsync("访客筛选根组", null);
            var childGroup = await classrooms.CreateGroupAsync("访客筛选子组", rootGroup.Id);
            var otherGroup = await classrooms.CreateGroupAsync("访客筛选其他组", null);
            var childClass = await classrooms.CreateAsync("访客筛选一班");
            var otherClass = await classrooms.CreateAsync("访客筛选二班");

            await classrooms.SetVisitorAccessAsync(childClass.Id, true);
            await classrooms.SetVisitorAccessAsync(otherClass.Id, true);
            await classrooms.SetClassGroupsAsync(childClass.Id, [childGroup.Id]);
            await classrooms.SetClassGroupsAsync(otherClass.Id, [otherGroup.Id]);
            rootGroupId = rootGroup.Id;
        }

        using var guest = CreateBrowser(factory);
        var response = await guest.GetAsync($"/Visitor?group={rootGroupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // 选择父分组时包含子分组中的班级；其他分组的班级不应出现在班级选项卡中。
        Assert.Contains("按分组筛选班级", html);
        Assert.Contains("访客筛选子组", html);
        Assert.Contains("访客筛选一班", html);
        Assert.DoesNotContain("访客筛选二班", html);
    }

    [Fact]
    public async Task VisitorPage_RedirectsToLoginWhenNoClassEnabled()
    {
        await using var factory = new TestWebApplicationFactory();
        using var guest = CreateBrowser(factory);
        var response = await guest.GetAsync("/Visitor");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LoginPage_OffersRememberMeAndUsesPersistentCookieWhenSelected()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = CreateBrowser(factory);

        var loginHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Login"));
        Assert.Contains("name=\"Input.RememberMe\"", loginHtml);
        Assert.Contains("保持登录", loginHtml);

        var token = Regex.Match(
            loginHtml,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(token.Success, "登录页必须包含 CSRF 令牌");
        using var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["Input.RememberMe"] = "true",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        }));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie");
        Assert.Contains(cookies, cookie => cookie.Contains("RemoteCI.Web=", StringComparison.Ordinal));
        Assert.Contains(cookies, cookie => cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AutoEnter_RequiresVisitorClassAndCanReturnToLogin()
    {
        await using var factory = new TestWebApplicationFactory();
        using var admin = CreateBrowser(factory);
        await LoginWebUiAsync(admin, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        // 没有任何班级开放访客时，即使保存了自动进入意图也不会真正进入访客页。
        var usersHtml = await admin.GetStringAsync("/Users");
        var saved = await PostRazorFormAsync(admin, "/Users?handler=VisitorAccess", usersHtml, new Dictionary<string, string>
        {
            ["AutoEnterVisitorPage"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>();
            Assert.False(await settings.AnyVisitorClassEnabledAsync());
        }
        using (var guestBefore = CreateBrowser(factory))
        {
            var stay = await guestBefore.GetAsync("/Login");
            Assert.Equal(HttpStatusCode.OK, stay.StatusCode);
        }

        // 开放默认班级访客功能后，保存的自动进入意图立即生效。
        var classesHtml = await admin.GetStringAsync("/Classes");
        var enabled = await PostRazorFormAsync(admin, "/Classes?handler=ToggleVisitor", classesHtml, new Dictionary<string, string>
        {
            ["id"] = TestWebApplicationFactory.DefaultClassId.ToString(),
            ["enabled"] = "true",
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
        var stay2 = await guest.GetAsync("/Login?from=visitor");
        Assert.Equal(HttpStatusCode.OK, stay2.StatusCode);
        var stayHtml = WebUtility.HtmlDecode(await stay2.Content.ReadAsStringAsync());
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
            await scope.ServiceProvider.GetRequiredService<ClassroomService>()
                .SetVisitorAccessAsync(TestWebApplicationFactory.DefaultClassId, true);
            await scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>()
                .SetAutoEnterAsync(true);
            var snapshot = await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CaptureAsync();
            Assert.Equal(5, snapshot.Version);
            Assert.NotNull(snapshot.Classrooms);
            Assert.Contains(snapshot.Classrooms!, x => x.Id == TestWebApplicationFactory.DefaultClassId && x.VisitorAccessEnabled);
            Assert.True(snapshot.Metadata.AutoEnterVisitorPage);
            await scope.ServiceProvider.GetRequiredService<ClassroomService>()
                .SetVisitorAccessAsync(TestWebApplicationFactory.DefaultClassId, false);
            await scope.ServiceProvider.GetRequiredService<VisitorAccessSettings>()
                .SetAutoEnterAsync(false);
            await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().ApplyAsync(snapshot);
        }

        using var verify = factory.Services.CreateScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<VisitorAccessSettings>().AnyVisitorClassEnabledAsync());
        Assert.True(await verify.ServiceProvider.GetRequiredService<VisitorAccessSettings>().GetAutoEnterAsync());

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
