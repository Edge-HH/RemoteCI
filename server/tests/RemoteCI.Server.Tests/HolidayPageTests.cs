using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayPageTests
{
    [Fact]
    public async Task AdminSeesPeriodsAndCanOverrideMakeupDay()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();
        using var browser = host.App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Holidays"));
        Assert.Contains("国庆节", html);
        Assert.Contains("2026-10-10（周六）调休上学", html);
        Assert.Contains("自动（周三）", html);
        Assert.Contains("href=\"/Holidays\"", html);

        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        var post = await browser.PostAsync("/Holidays?handler=Override", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = "2026-10-10",
            ["follow"] = "skip",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var overview = await host.Service.GetOverviewAsync();
        Assert.Contains(overview.Periods.SelectMany(x => x.MakeupDays), x => x is { Date: "2026-10-10", FollowSource: HolidayFollowSources.Skip });
    }

    [Fact]
    public async Task ScheduleTableShowsHolidayTags()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(Classroom.DefaultId, new ScheduleBundle
            {
                FromDate = "2026-10-01",
                Days =
                [
                    new ScheduleDay { Date = "2026-10-01", DayKind = ScheduleDayKinds.Holiday, HolidayName = "国庆节" },
                    new ScheduleDay { Date = "2026-10-10", Enabled = true, DayKind = ScheduleDayKinds.Makeup, HolidayName = "国庆节" },
                ],
            });
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        Assert.Contains("放假 · 国庆节", html);
        Assert.Contains("调休 · 国庆节", html);
    }

    private static async Task LoginAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }
}
