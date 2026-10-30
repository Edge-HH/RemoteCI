using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class PersonalScheduleTests
{
    private static readonly TimeSpan China = TimeSpan.FromHours(8);

    private static PersonalCourseRequest Weekly(string subject, DayOfWeek day, string start = "08:00", string end = "08:45",
        int div = 0, int total = 2, int? reminder = null, bool notify = false) => new()
    {
        Subject = subject, WeekDay = (int)day, StartTime = start, EndTime = end, WeekCountDiv = div, WeekCountDivTotal = total,
        TimeZoneOffsetMinutes = 480, ReminderMinutes = reminder, NotifyAtStart = notify,
    };

    private static async Task SetSemesterAsync(TestWebApplicationFactory factory, string? start)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SemesterSettingsService>().SetAsync(new SemesterSettings { StartDate = start });
    }

    [Fact]
    public async Task WebUi_SavesWeekdayTimeAndRotation_AndShowsApiChanges()
    {
        await using var factory = new TestWebApplicationFactory();
        await SetSemesterAsync(factory, "2030-09-02");
        var owner = await factory.LoginAsync();
        using var api = factory.CreateClient();
        var ticket = await api.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/auth/web-ticket", owner.AccessToken));
        var path = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await browser.GetAsync(path)).EnsureSuccessStatusCode();
        var page = await browser.GetAsync("/MySchedule");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("type=\"time\"", html);
        Assert.Contains("name=\"WeekDay\"", html);
        Assert.DoesNotContain("datetime-local", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Subject"] = "私人日程表单测试", ["Location"] = "",
            ["WeekDay"] = "3", ["StartTime"] = "14:00", ["EndTime"] = "14:45", ["Rotation"] = "2/2", ["TimeZoneOffsetMinutes"] = "480",
            ["ReminderMinutes"] = "", ["NotifyAtStart"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/MySchedule?handler=Save", form)).StatusCode);
        var courses = await browser.GetFromJsonAsync<List<PersonalCourseView>>("/MySchedule?handler=Courses");
        var course = Assert.Single(courses!);
        Assert.Equal(3, course.WeekDay);
        Assert.Equal("14:00", course.StartTime);
        Assert.Equal("14:45", course.EndTime);
        Assert.Equal(2, course.WeekCountDiv);
        Assert.Equal(480, course.TimeZoneOffsetMinutes);
        Assert.Equal("每周三 14:00–14:45 · 双周", course.Summary);
        Assert.Null(course.Date);
        Assert.Null(course.ReminderMinutes);
        Assert.False(course.NotifyAtStart);
        var update = await api.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken,
            Weekly("通过另一设备修改", DayOfWeek.Friday)));
        update.EnsureSuccessStatusCode();
        courses = await browser.GetFromJsonAsync<List<PersonalCourseView>>("/MySchedule?handler=Courses");
        Assert.Equal("通过另一设备修改", Assert.Single(courses!).Subject);
        html = WebUtility.HtmlDecode(await browser.GetStringAsync("/MySchedule"));
        Assert.Contains("每周五 08:00–08:45", html);
    }

    [Fact]
    public async Task Courses_AreAccountPrivate_RepeatWeekly_AndIncludedInNext()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var createUser = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "personal-other", DisplayName = "personal-other", Password = "Test-Password-123", RoleId = AccountRole.TeacherId }));
        createUser.EnsureSuccessStatusCode();
        // 新账号不能与他人同名；直接写库模拟升级前遗留的同名账号，确认私人课程按账号而不是用户名隔离。
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(x => x.UserName == "personal-other")
                .ExecuteUpdateAsync(x => x.SetProperty(user => user.DisplayName, owner.User.DisplayName));
        var other = await factory.LoginAsync("personal-other", "Test-Password-123");
        var body = Weekly("私人研讨", DayOfWeek.Thursday, "19:30", "20:30", reminder: 25, notify: true);
        body.Location = "自习室";
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, body));
        response.EnsureSuccessStatusCode();
        var course = (await response.Content.ReadFromJsonAsync<PersonalCourseView>())!;
        // 旧客户端仍能读到下一次课的绝对时间。
        Assert.Equal(DayOfWeek.Thursday, course.StartsAt!.Value.DayOfWeek);
        Assert.Equal(new TimeSpan(19, 30, 0), course.StartsAt.Value.TimeOfDay);
        var anotherDevice = await factory.LoginAsync();
        var list = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule/courses", anotherDevice.AccessToken));
        Assert.Equal(course.Id, Assert.Single((await list.Content.ReadFromJsonAsync<List<PersonalCourseView>>())!).Id);
        var hidden = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule/courses", other.AccessToken));
        Assert.Empty((await hidden.Content.ReadFromJsonAsync<List<PersonalCourseView>>())!);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            var denied = await client.SendAsync(TestWebApplicationFactory.Bearer(method, $"/api/me/schedule/courses/{course.Id}", other.AccessToken,
                method == HttpMethod.Put ? body : null));
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        var schedule = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule", owner.AccessToken));
        var day = Assert.Single((await schedule.Content.ReadFromJsonAsync<MyScheduleResponse>())!.Days);
        Assert.Equal(DayOfWeek.Thursday, DateOnly.Parse(day.Date).DayOfWeek);
        var item = Assert.Single(day.Items);
        Assert.Equal("personal", item.Source);
        Assert.Equal("自习室", item.ClassName);
        Assert.Equal(course.Id, Assert.Single(item.Courses).PersonalCourseId);
        // 任意一周的周四都能找到这节课。
        foreach (var thursday in new[] { new DateTimeOffset(2030, 10, 17, 19, 30, 0, China), new DateTimeOffset(2031, 3, 6, 19, 30, 0, China) })
        {
            var at = Uri.EscapeDataString(thursday.AddMinutes(20).ToString("O"));
            var nextResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/me/schedule/next?at={at}", owner.AccessToken));
            var next = (await nextResponse.Content.ReadFromJsonAsync<MyNextCourseResponse>())!;
            Assert.Equal(thursday, next.Current!.StartsAt);
            Assert.Equal(thursday.AddHours(1), next.Current.EndsAt);
            Assert.Equal(thursday.AddDays(7), next.Next!.StartsAt);
        }
        body.ReminderMinutes = null;
        body.NotifyAtStart = false;
        var update = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken, body));
        Assert.Null((await update.Content.ReadFromJsonAsync<PersonalCourseView>())!.ReminderMinutes);
        var delete = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Delete, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Rotation_FollowsSemesterTeachingWeeks_AndRequiresSemesterStart()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var odd = Weekly("单周课", DayOfWeek.Monday, div: 1, total: 2);
        var rejected = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, odd));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("学期开始日期", await rejected.Content.ReadAsStringAsync());

        // 学期从 2030-09-04（周三）开始：第 1 教学周从 9 月 2 日（周一）算起。
        var semester = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/settings/semester", owner.AccessToken,
            new SemesterSettings { StartDate = "2030-09-04", SyncToClassrooms = false }));
        semester.EnsureSuccessStatusCode();
        Assert.Equal("2030-09-04", (await semester.Content.ReadFromJsonAsync<SemesterSettingsView>())!.StartDate);
        Guid oddId, thirdId;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PersonalScheduleService>();
            oddId = (await service.SaveAsync(owner.User.Id, null, odd)).Id;
            thirdId = (await service.SaveAsync(owner.User.Id, null, Weekly("三周轮换第三周", DayOfWeek.Monday, "10:00", "10:45", div: 3, total: 3))).Id;
            var start = new DateOnly(2030, 9, 4);
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PersonalCourses.AsNoTracking().ToListAsync();
            List<DateOnly> Dates(Guid id) => PersonalScheduleService.Occurrences(rows.Single(x => x.Id == id),
                new DateOnly(2030, 9, 1), new DateOnly(2030, 10, 31), start).Select(x => x.Date).ToList();
            Assert.Equal([new(2030, 9, 2), new(2030, 9, 16), new(2030, 9, 30), new(2030, 10, 14), new(2030, 10, 28)], Dates(oddId));
            Assert.Equal([new(2030, 9, 16), new(2030, 10, 7), new(2030, 10, 28)], Dates(thirdId));
        }
        foreach (var invalid in new[] { Weekly("x", DayOfWeek.Monday, div: 3, total: 2), Weekly("x", DayOfWeek.Monday, div: 1, total: 5),
                     Weekly("x", DayOfWeek.Monday, "09:00", "08:00"), Weekly("x", DayOfWeek.Monday, "8点", "9点") })
        {
            var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, invalid));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // 清除学期后轮换课程暂不出现，避免在错误的周提醒；每周课程不受影响。
        await SetSemesterAsync(factory, null);
        using (var scope = factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PersonalCourses.AsNoTracking().SingleAsync(x => x.Id == oddId);
            Assert.Empty(PersonalScheduleService.Occurrences(row, new DateOnly(2030, 9, 1), new DateOnly(2030, 10, 31), null));
        }
    }

    [Fact]
    public async Task SemesterSettings_AreAdminOnly_AndReportCurrentWeek()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var createUser = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "semester-teacher", DisplayName = "教师", Password = "Test-Password-123", RoleId = AccountRole.TeacherId }));
        createUser.EnsureSuccessStatusCode();
        var teacher = await factory.LoginAsync("semester-teacher", "Test-Password-123");
        var empty = await (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/settings/semester", teacher.AccessToken)))
            .Content.ReadFromJsonAsync<SemesterSettingsView>();
        Assert.Null(empty!.StartDate);
        Assert.Null(empty.CurrentWeek);
        var denied = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/settings/semester", teacher.AccessToken,
            new SemesterSettings { StartDate = "2026-09-01" }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var invalid = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/settings/semester", owner.AccessToken,
            new SemesterSettings { StartDate = "2026/09/01" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var saved = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/settings/semester", owner.AccessToken,
            new SemesterSettings { StartDate = SemesterWeeks.Format(today.AddDays(-14)) }));
        var view = (await saved.Content.ReadFromJsonAsync<SemesterSettingsView>())!;
        Assert.True(view.SyncToClassrooms);
        Assert.InRange(view.CurrentWeek!.Value, 3, 4);
    }

    [Fact]
    public async Task Reminders_RepeatEachWeek_RespectSettings_AndDoNotRepeatWithinOneLesson()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        var start = new DateTimeOffset(2031, 1, 1, 8, 0, 0, China);
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PersonalScheduleService>();
            await service.SaveAsync(owner.User.Id, null, Weekly("提醒测试", start.DayOfWeek, reminder: 25, notify: true));
            await service.SaveAsync(owner.User.Id, null, Weekly("静音课程", start.DayOfWeek));
        }
        foreach (var lesson in new[] { start, start.AddDays(7) })
            foreach (var now in new[] { lesson.AddMinutes(-26), lesson.AddMinutes(-25), lesson.AddMinutes(-24), lesson, lesson.AddSeconds(15) })
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PersonalCourseReminderService>().SendDueAsync(now);
            }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notes = await db.UserNotifications.Where(x => x.UserId == owner.User.Id).ToListAsync();
            Assert.Equal(4, notes.Count);
            Assert.Equal(2, notes.Count(x => x.Kind == "personal_course_upcoming"));
            Assert.Equal(2, notes.Count(x => x.Kind == "personal_course_start"));
            Assert.All(notes, x => Assert.Contains("提醒测试", x.Title));
            Assert.Contains(notes, x => x.Body.StartsWith("2031-01-08 08:00", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LegacyStartsAtRequests_CreateOneOffCourses()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var start = new DateTimeOffset(2030, 12, 1, 14, 0, 0, China);
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken,
            new PersonalCourseRequest { Subject = "AstrBot 单次课", StartsAt = start, EndsAt = start.AddHours(1) }));
        response.EnsureSuccessStatusCode();
        var course = (await response.Content.ReadFromJsonAsync<PersonalCourseView>())!;
        Assert.Equal("2030-12-01", course.Date);
        Assert.Equal((int)DayOfWeek.Sunday, course.WeekDay);
        Assert.Equal(start, course.StartsAt);
        Assert.Equal(start.AddHours(1), course.EndsAt);
        Assert.EndsWith("单次", course.Summary);
        var at = Uri.EscapeDataString(start.AddDays(6).ToString("O"));
        var next = await (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/me/schedule/next?at={at}", owner.AccessToken)))
            .Content.ReadFromJsonAsync<MyNextCourseResponse>();
        Assert.Null(next!.Next);
        var crossing = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken,
            new PersonalCourseRequest { Subject = "跨午夜", StartsAt = start.AddHours(9.5), EndsAt = start.AddHours(10.5) }));
        Assert.Equal(HttpStatusCode.BadRequest, crossing.StatusCode);
    }

    [Fact]
    public async Task Migration_KeepsExistingCoursesAsOneOffLessons()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261018232925_AddClassBoards");
        var start = new DateTimeOffset(2026, 10, 20, 23, 30, 0, China);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO PersonalCourses (Id, UserId, Subject, Location, StartsAtUnixMs, EndsAtUnixMs, TimeZoneOffsetMinutes, ReminderMinutes, NotifyAtStart, UpcomingSent, StartSent)
            VALUES ({Guid.NewGuid().ToString().ToUpperInvariant()}, {owner.User.Id.ToString().ToUpperInvariant()}, '旧课程', '', {start.ToUnixTimeMilliseconds()}, {start.AddHours(1).ToUnixTimeMilliseconds()}, 480, 10, 1, 1, 0)
            """);
        await migrator.MigrateAsync();
        var row = await db.PersonalCourses.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 20), row.Date);
        Assert.Equal((int)DayOfWeek.Tuesday, row.WeekDay);
        Assert.Equal(23 * 60 + 30, row.StartMinutes);
        Assert.Equal(1439, row.EndMinutes);
        Assert.Equal(start.ToUnixTimeMilliseconds(), row.UpcomingSentFor);
        Assert.Equal(0, row.StartSentFor);
        Assert.Equal(2, row.WeekCountDivTotal);
        Assert.True(await db.SystemMetadata.Select(x => x.SemesterSyncToClassrooms).SingleAsync());
    }

    [Fact]
    public void SemesterWeeks_MatchClassIslandRotation()
    {
        var start = new DateOnly(2026, 9, 2); // 周三，第 1 周从 8 月 31 日周一开始
        Assert.Equal(new DateOnly(2026, 8, 31), SemesterWeeks.Monday(start));
        Assert.Equal(0, SemesterWeeks.WeekIndex(start, new DateOnly(2026, 9, 6)));
        Assert.Equal(1, SemesterWeeks.WeekIndex(start, new DateOnly(2026, 9, 7)));
        Assert.Equal(-1, SemesterWeeks.WeekIndex(start, new DateOnly(2026, 8, 30)));
        Assert.Null(SemesterWeeks.TeachingWeek(start, new DateOnly(2026, 8, 30)));
        Assert.Equal(2, SemesterWeeks.TeachingWeek(start, new DateOnly(2026, 9, 7)));
        Assert.True(SemesterWeeks.InRotation(1, 2, 0));
        Assert.False(SemesterWeeks.InRotation(1, 2, 1));
        Assert.True(SemesterWeeks.InRotation(2, 2, -1));
        Assert.True(SemesterWeeks.InRotation(3, 3, 5));
        Assert.True(SemesterWeeks.InRotation(0, 2, 7));
        Assert.Equal("单周", SemesterWeeks.RotationLabel(1, 2));
        Assert.Equal("3 周轮换第 2 周", SemesterWeeks.RotationLabel(2, 3));
    }

    [Fact]
    public async Task InvalidCourses_AreRejected_AndAnonymousRequestsAreDenied()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/schedule/courses")).StatusCode);
        var owner = await factory.LoginAsync();
        var start = DateTimeOffset.UtcNow.AddDays(30);
        foreach (var body in new[] {
            Weekly(" ", DayOfWeek.Monday),
            new PersonalCourseRequest { Subject = "测试" },
            new PersonalCourseRequest { Subject = "测试", WeekDay = 7, StartTime = "08:00", EndTime = "09:00" },
            new PersonalCourseRequest { Subject = "测试", StartsAt = start, EndsAt = start },
            Weekly("测试", DayOfWeek.Monday, reminder: -1),
        })
        {
            var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
