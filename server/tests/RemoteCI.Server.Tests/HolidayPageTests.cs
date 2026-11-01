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
        var monday = SemesterWeeks.Monday(ClassClock.Today(factory.Services.GetRequiredService<IStateStore>(), TestWebApplicationFactory.DefaultClassId));
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(TestWebApplicationFactory.DefaultClassId, new ScheduleBundle
            {
                FromDate = SemesterWeeks.Format(monday),
                Days =
                [
                    new ScheduleDay { Date = SemesterWeeks.Format(monday), DayKind = ScheduleDayKinds.Holiday, HolidayName = "国庆节" },
                    new ScheduleDay { Date = SemesterWeeks.Format(monday.AddDays(1)), Enabled = true, DayKind = ScheduleDayKinds.Makeup, HolidayName = "国庆节" },
                    new ScheduleDay { Date = SemesterWeeks.Format(monday.AddDays(6)), Enabled = true },
                ],
            });
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        Assert.Contains("放假 · 国庆节", html);
        Assert.Contains("<em class=\"sg-day-tag makeup\">调休</em>", html);
    }

    [Fact]
    public async Task ScheduleTableOrdersMondayFirstAndMarksToday()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        var today = ClassClock.Today(factory.Services.GetRequiredService<IStateStore>(), TestWebApplicationFactory.DefaultClassId);
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(TestWebApplicationFactory.DefaultClassId, new ScheduleBundle
            {
                FromDate = today.ToString("yyyy-MM-dd"),
                Days = Enumerable.Range(0, 7)
                    .Select(offset => new ScheduleDay { Date = today.AddDays(offset).ToString("yyyy-MM-dd"), Enabled = true })
                    .ToList(),
            });
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        var headings = Regex.Matches(html, "class=\"sg-day-head[^\"]*\"[^>]*>\\s*<strong>(周.)</strong>").Select(x => x.Groups[1].Value).ToList();
        Assert.Equal(["周一", "周二", "周三", "周四", "周五", "周六", "周日"], headings);
        Assert.Matches($"class=\"sg-day-head sg-today[^\"]*\" aria-current=\"date\">\\s*<strong>周.</strong>\\s*<small>{today:MM/dd}</small>", html);
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
