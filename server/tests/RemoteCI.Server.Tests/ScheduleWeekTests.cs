using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>按周浏览课表（过去取课时台账、今天起取插件课表）与学科颜色接口。</summary>
public sealed class ScheduleWeekTests
{
    private static readonly Guid ClassId = TestWebApplicationFactory.DefaultClassId;
    private static readonly Guid InfoId = Guid.NewGuid();
    private static readonly Guid MathId = Guid.NewGuid();

    // 服务端在没有状态快照时按本机时区确定“今天”，测试与之一致。
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    private static string Day(int offset) => SemesterWeeks.Format(Today.AddDays(offset));

    private static async Task SeedAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SemesterSettingsService>()
            .SetAsync(new SemesterSettings { StartDate = Day(-60) });
        scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(ClassId, new ScheduleBundle
        {
            FromDate = Day(0),
            Days = Enumerable.Range(0, 3).Select(offset => new ScheduleDay
            {
                Date = Day(offset), Revision = $"r{offset}", Enabled = true, DividerAfterIndexes = [0],
                Courses =
                [
                    new CourseEntry { Index = 0, Label = "第 1 节", SubjectId = InfoId, Subject = "信息", StartTime = "08:00", EndTime = "08:45", Teacher = "王老师", Location = "机房", Enabled = true },
                    new CourseEntry { Index = 1, Label = "第 2 节", SubjectId = MathId, Subject = "数学", StartTime = "08:55", EndTime = "09:40", Teacher = "李老师", Enabled = true },
                ],
            }).ToList(),
            Subjects =
            [
                new SubjectEntry { Id = InfoId, Name = "信息", Teacher = "王老师", Location = "机房" },
                new SubjectEntry { Id = MathId, Name = "数学", Teacher = "李老师" },
            ],
        });
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LessonLedgerEntries.AddRange(
            new LessonLedgerEntry { ClassId = ClassId, ClassName = "测试班", Date = Day(-1), Index = 0, Label = "第 1 节", Subject = "信息", StartTime = "08:00", EndTime = "08:45", TeacherName = "王老师" },
            new LessonLedgerEntry { ClassId = ClassId, ClassName = "测试班", Date = Day(-1), Index = 1, Label = "第 2 节", Subject = "体育", StartTime = "08:55", EndTime = "09:40", TeacherName = "赵老师" });
        db.ScheduleAdjustments.Add(new ScheduleAdjustment
        {
            Id = Guid.NewGuid(), ClassId = ClassId, Date = Day(-1), Index = 1, Sequence = 1,
            FromSubjectId = MathId, FromSubject = "数学", ToSubjectId = Guid.NewGuid(), ToSubject = "体育",
            CreatedByName = "测试", Status = ScheduleAdjustmentStatus.Expired,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ClassWeek_UsesLedgerForPastDaysAndBundleFromToday()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get,
            $"/api/schedule/week?classId={ClassId}&from={Day(-3)}&to={Day(3)}", owner.AccessToken));
        response.EnsureSuccessStatusCode();
        var week = (await response.Content.ReadFromJsonAsync<ScheduleWeekResponse>())!;

        Assert.Equal(Day(0), week.Today);
        Assert.Equal(Day(2), week.MaxDate);
        Assert.Equal(SemesterWeeks.Format(SemesterWeeks.Monday(Today.AddDays(-60))), week.MinDate);
        Assert.Equal([Day(-3), Day(-2), Day(-1), Day(0), Day(1), Day(2)], week.Days.Select(x => x.Date));
        Assert.Empty(week.Days[0].Courses);

        var yesterday = week.Days[2];
        Assert.Equal(["信息", "体育"], yesterday.Courses.Select(x => x.Subject));
        Assert.Equal("机房", yesterday.Courses[0].Location);
        Assert.Equal("王老师", yesterday.Courses[0].Teacher);
        Assert.Null(yesterday.Courses[0].AdjustmentId);
        Assert.NotNull(yesterday.Courses[1].AdjustmentId);
        Assert.Equal([0], yesterday.DividerAfterIndexes!);
        Assert.Equal("数学", week.Days[3].Courses[1].Subject);

        Assert.Equal(["0:08:00-08:45", "1:08:55-09:40"], week.Periods.Select(p => $"{p.Index}:{p.StartTime}-{p.EndTime}"));
        Assert.True(week.Periods[1].DividerBefore);
        Assert.Equal(SemesterWeeks.TeachingWeek(Today.AddDays(-60), Today.AddDays(-3)), week.TeachingWeek);

        var defaults = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/schedule/week?classId={ClassId}", owner.AccessToken));
        Assert.Equal(SemesterWeeks.Format(SemesterWeeks.Monday(Today)), (await defaults.Content.ReadFromJsonAsync<ScheduleWeekResponse>())!.From);

        foreach (var bad in new[] { $"from={Day(1)}&to={Day(0)}", $"from={Day(0)}&to={Day(42)}", "from=2026/11/02", $"to={Day(0)}" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/schedule/week?classId={ClassId}&{bad}", owner.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/schedule/week?classId={ClassId}")).StatusCode);
    }

    [Fact]
    public async Task MyWeek_MatchesTeacherInLedgerAndBundleAndAddsPersonalCourses()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "week-teacher", DisplayName = "王老师", Password = "Test-Password-123", RoleId = AccountRole.TeacherId })))
            .EnsureSuccessStatusCode();
        var teacher = await factory.LoginAsync("week-teacher", "Test-Password-123");
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", teacher.AccessToken,
            new PersonalCourseRequest
            {
                Subject = "教研会", Location = "报告厅", WeekDay = (int)Today.DayOfWeek, StartTime = "19:00", EndTime = "20:00",
                TimeZoneOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
            }))).EnsureSuccessStatusCode();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get,
            $"/api/me/schedule/week?from={Day(-1)}&to={Day(0)}", teacher.AccessToken));
        response.EnsureSuccessStatusCode();
        var week = (await response.Content.ReadFromJsonAsync<MyScheduleWeekResponse>())!;

        Assert.Equal([Day(-1), Day(0)], week.Days.Select(x => x.Date));
        var past = Assert.Single(week.Days[0].Items);
        Assert.Equal(ClassId, past.ClassId);
        Assert.Equal(["信息"], past.Courses.Select(x => x.Subject));
        Assert.Equal("机房", past.Courses[0].Location);
        var today = week.Days[1].Items;
        var lesson = Assert.Single(today, x => x.ClassId == ClassId);
        Assert.Equal(["信息"], lesson.Courses.Select(x => x.Subject));
        Assert.Equal([0], lesson.DividerAfterIndexes!);
        var personal = Assert.Single(today, x => x.Source == PersonalScheduleService.Source);
        Assert.Equal("报告厅", Assert.Single(personal.Courses).Location);
        // 节次轴取完整的班级时间表：老师不上的第 2 节也留出一行。
        Assert.Equal([0, 1], week.Periods.Select(x => x.Index));
    }

    [Fact]
    public async Task ClassCalendarFeed_AppendsSubjectLocationToClassName()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);
        var owner = await factory.LoginAsync();
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Id == owner.User.Id);
            token = (await scope.ServiceProvider.GetRequiredService<CalendarFeedService>().CreateAsync(user, ClassId, null, 480, default)).Token;
        }
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync($"/calendar/{token}.ics");
        Assert.Contains(body.Split("\r\n"), line => line.StartsWith("LOCATION:", StringComparison.Ordinal) && line.EndsWith(" · 机房", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebPages_RenderWeekGridNavigationAndSaveSubjectColor()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);
        var owner = await factory.LoginAsync();
        using var api = factory.CreateClient();
        var ticket = await api.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/auth/web-ticket", owner.AccessToken));
        var path = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await browser.GetAsync(path)).EnsureSuccessStatusCode();

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));
        Assert.Contains("<span class=\"sg-location\">机房</span>", html);
        Assert.Contains("<span class=\"sg-teacher\">王老师</span>", html);
        Assert.Contains("data-sg-color-form", html);
        Assert.Contains($"第 {SemesterWeeks.TeachingWeek(Today.AddDays(-60), Today)} 周", html);

        // 早于学期开始的周没有数据，也不能再往前翻。
        var early = SemesterWeeks.Format(SemesterWeeks.Monday(Today.AddDays(-60)).AddDays(-7));
        var earlyHtml = WebUtility.HtmlDecode(await browser.GetStringAsync($"/Schedule?week={early}"));
        Assert.Equal(7, Regex.Matches(earlyHtml, "暂无课表数据").Count);
        Assert.Matches("aria-disabled=\"true\"[^>]*>上一周", earlyHtml);
        Assert.Contains("回到本周", earlyHtml);

        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Schedule?handler=SubjectColor")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["name"] = "信息", ["color"] = SubjectColors.Palette[2],
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        var saved = await browser.SendAsync(request);
        saved.EnsureSuccessStatusCode();
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("custom").GetBoolean());
        Assert.Equal(SubjectColors.Derive(SubjectColors.Palette[2], dark: false).Background, result.GetProperty("light").GetProperty("background").GetString());
        var recolored = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));
        Assert.Contains($"--sc-bg:{SubjectColors.Derive(SubjectColors.Palette[2], dark: false).Background}", recolored);

        var mine = WebUtility.HtmlDecode(await browser.GetStringAsync("/MySchedule"));
        Assert.Contains("data-my-view-button=\"table\"", mine);
        Assert.Contains("data-my-view-panel=\"list\"", mine);
        Assert.Contains("data-schedule-grid", mine);
    }

    [Fact]
    public async Task SubjectColors_ListSetAndResetPerAccount()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();

        async Task<SubjectColorsResponse> ListAsync() =>
            (await (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/subject-colors", owner.AccessToken)))
                .Content.ReadFromJsonAsync<SubjectColorsResponse>())!;

        var initial = await ListAsync();
        Assert.Equal(SubjectColors.Palette, initial.Palette);
        var info = Assert.Single(initial.Items, x => x.Name == "信息");
        Assert.Equal(SubjectColors.Default("信息"), info.Color);
        Assert.False(info.Custom);
        Assert.Contains(initial.Items, x => x.Name == "数学");

        var invalid = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/me/subject-colors", owner.AccessToken,
            new SubjectColorRequest { Name = "信息", Color = "#123456" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var set = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/me/subject-colors", owner.AccessToken,
            new SubjectColorRequest { Name = " 信息 ", Color = SubjectColors.Palette[5].ToLowerInvariant() }));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        // 改过颜色、但已不在任何课表中的学科仍然列出，便于恢复默认。
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/me/subject-colors", owner.AccessToken,
            new SubjectColorRequest { Name = "已停开的课", Color = SubjectColors.Palette[1] }))).EnsureSuccessStatusCode();

        var changed = await ListAsync();
        Assert.Equal(SubjectColors.Palette[5], Assert.Single(changed.Items, x => x.Name == "信息").Color);
        Assert.True(Assert.Single(changed.Items, x => x.Name == "已停开的课").Custom);

        var reset = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Delete, "/api/me/subject-colors?name=信息", owner.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.False(Assert.Single((await ListAsync()).Items, x => x.Name == "信息").Custom);
    }
}
