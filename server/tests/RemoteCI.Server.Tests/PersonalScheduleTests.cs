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

public sealed class PersonalScheduleTests
{
    [Fact]
    public async Task WebUi_CanCreateWithEmptyLocationAndBothRemindersDisabled_AndShowsApiChanges()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var api = factory.CreateClient();
        var ticket = await api.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/auth/web-ticket", owner.AccessToken));
        var path = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await browser.GetAsync(path)).EnsureSuccessStatusCode();
        var page = await browser.GetAsync("/MySchedule");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var start = new DateTimeOffset(2030, 12, 1, 8, 0, 0, TimeSpan.FromHours(8));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Subject"] = "私人日程表单测试", ["Location"] = "",
            ["StartsAt"] = start.ToString("O"), ["EndsAt"] = start.AddHours(1).ToString("O"),
            ["ReminderMinutes"] = "", ["NotifyAtStart"] = "false",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/MySchedule?handler=Save", form)).StatusCode);
        var courses = await browser.GetFromJsonAsync<List<PersonalCourseView>>("/MySchedule?handler=Courses");
        var course = Assert.Single(courses!);
        Assert.Null(course.ReminderMinutes);
        Assert.False(course.NotifyAtStart);
        Assert.Equal("", course.Location);
        var update = await api.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken,
            new PersonalCourseRequest { Subject = "通过另一设备修改", StartsAt = start, EndsAt = start.AddHours(1) }));
        update.EnsureSuccessStatusCode();
        courses = await browser.GetFromJsonAsync<List<PersonalCourseView>>("/MySchedule?handler=Courses");
        Assert.Equal("通过另一设备修改", Assert.Single(courses!).Subject);
    }

    [Fact]
    public async Task Courses_AreAccountPrivate_SyncedAcrossSessions_AndIncludedInNext()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var createUser = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "personal-other", DisplayName = owner.User.DisplayName, Password = "Test-Password-123", RoleId = AccountRole.TeacherId }));
        createUser.EnsureSuccessStatusCode();
        var other = await factory.LoginAsync("personal-other", "Test-Password-123");
        var start = new DateTimeOffset(2030, 10, 18, 23, 30, 0, TimeSpan.FromHours(8));
        var body = new PersonalCourseRequest { Subject = "私人研讨", Location = "自习室", StartsAt = start,
            EndsAt = start.AddHours(1), ReminderMinutes = 25, NotifyAtStart = true };
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, body));
        response.EnsureSuccessStatusCode();
        var course = (await response.Content.ReadFromJsonAsync<PersonalCourseView>())!;
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
        var item = Assert.Single(Assert.Single((await schedule.Content.ReadFromJsonAsync<MyScheduleResponse>())!.Days).Items);
        Assert.Equal("personal", item.Source);
        Assert.Equal(course.Id, Assert.Single(item.Courses).PersonalCourseId);
        var at = Uri.EscapeDataString(start.AddMinutes(20).ToString("O"));
        var nextResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/me/schedule/next?at={at}", owner.AccessToken));
        var slot = (await nextResponse.Content.ReadFromJsonAsync<MyNextCourseResponse>())!.Current!;
        Assert.Equal(start, slot.StartsAt);
        Assert.Equal(start.AddHours(1), slot.EndsAt);
        body.ReminderMinutes = null;
        body.NotifyAtStart = false;
        var update = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken, body));
        Assert.Null((await update.Content.ReadFromJsonAsync<PersonalCourseView>())!.ReminderMinutes);
        var delete = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Delete, $"/api/me/schedule/courses/{course.Id}", owner.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Reminders_SurviveNewScopes_RespectSettings_AndDoNotRepeat()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        var start = new DateTimeOffset(2031, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PersonalScheduleService>();
            await service.SaveAsync(owner.User.Id, null, new PersonalCourseRequest { Subject = "提醒测试", StartsAt = start,
                EndsAt = start.AddHours(1), ReminderMinutes = 25, NotifyAtStart = true });
            await service.SaveAsync(owner.User.Id, null, new PersonalCourseRequest { Subject = "静音课程", StartsAt = start,
                EndsAt = start.AddHours(1), ReminderMinutes = null, NotifyAtStart = false });
        }
        foreach (var now in new[] { start.AddMinutes(-26), start.AddMinutes(-25), start.AddMinutes(-24), start, start.AddSeconds(15) })
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PersonalCourseReminderService>().SendDueAsync(now);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notes = await db.UserNotifications.Where(x => x.UserId == owner.User.Id).ToListAsync();
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, x => x.Kind == "personal_course_upcoming");
            Assert.Contains(notes, x => x.Kind == "personal_course_start");
            Assert.All(notes, x => Assert.Contains("提醒测试", x.Title));
        }
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
            new PersonalCourseRequest { Subject = " ", StartsAt = start, EndsAt = start.AddHours(1) },
            new PersonalCourseRequest { Subject = "测试", StartsAt = start, EndsAt = start },
            new PersonalCourseRequest { Subject = "测试", StartsAt = start, EndsAt = start.AddHours(1), ReminderMinutes = -1 },
        })
        {
            var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/me/schedule/courses", owner.AccessToken, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
