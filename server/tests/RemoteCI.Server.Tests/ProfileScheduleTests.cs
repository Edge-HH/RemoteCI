using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>插件尚未同步课表时，课表页按班级当前档案推算课表。</summary>
public sealed class ProfileScheduleTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    [Fact]
    public void BuildSchedule_UsesWeekdayPlanWithTimesAndTeacher()
    {
        var result = ProfileDocument.BuildSchedule(ProfileTestData.Json(), Monday);

        Assert.Equal(ScheduleBundleSources.Profile, result.Bundle.Source);
        Assert.Equal("2026-10-12", result.Bundle.FromDate);
        Assert.Equal(7, result.Bundle.Days.Count);
        var monday = result.Bundle.Days[0];
        Assert.True(monday.Enabled);
        Assert.Equal("周一课表", monday.ClassPlanName);
        var course = Assert.Single(monday.Courses);
        Assert.Equal("语文", course.Subject);
        Assert.Equal("08:00", course.StartTime);
        Assert.Equal("08:40", course.EndTime);
        Assert.Equal("原教师", course.Teacher);
        Assert.StartsWith("profile:", monday.Revision);
        Assert.All(result.Bundle.Days.Skip(1), day => Assert.False(day.Enabled));
        Assert.Contains(result.Bundle.Subjects, x => x.Name == "语文" && x.Teacher == "原教师");
        Assert.False(result.UsesWeekRotation);
    }

    [Fact]
    public void BuildSchedule_AppliesHolidayOffAndMakeupDays()
    {
        var calendar = new Dictionary<DateOnly, HolidayCalendarDay>
        {
            [Monday] = new() { Date = "2026-10-12", Kind = HolidayDayKinds.Off, Name = "测试假期" },
            [Monday.AddDays(5)] = new() { Date = "2026-10-17", Kind = HolidayDayKinds.Makeup, Name = "测试假期", FollowWeekday = 1 },
        };
        var bundle = ProfileDocument.BuildSchedule(ProfileTestData.Json(), Monday, holidays: date => calendar.GetValueOrDefault(date)).Bundle;

        Assert.False(bundle.Days[0].Enabled);
        Assert.Empty(bundle.Days[0].Courses);
        Assert.Equal(ScheduleDayKinds.Holiday, bundle.Days[0].DayKind);
        Assert.True(bundle.Days[5].Enabled);
        Assert.Equal(ScheduleDayKinds.Makeup, bundle.Days[5].DayKind);
        Assert.Equal("语文", Assert.Single(bundle.Days[5].Courses).Subject);
    }

    [Fact]
    public void BuildSchedule_PrefersOrderedTempLayerWhenEnabled()
    {
        var profile = ProfileDocument.Parse(ProfileTestData.WithTempLayer(ProfileTestData.Json(), "2026-10-13"));
        Assert.False(ProfileDocument.BuildSchedule(ProfileDocument.Serialize(profile), Monday).Bundle.Days[1].Enabled);

        profile["IsOverlayClassPlanEnabled"] = true;
        var tuesday = ProfileDocument.BuildSchedule(ProfileDocument.Serialize(profile), Monday).Bundle.Days[1];
        Assert.True(tuesday.Enabled);
        Assert.Equal("周一课表（临时层）", tuesday.ClassPlanName);
    }

    [Fact]
    public void BuildSchedule_RotatesWeeksFromCurrentWeek()
    {
        var profile = ProfileDocument.Parse(ProfileTestData.Json());
        var plans = profile["ClassPlans"]!.AsObject();
        var odd = plans[ProfileTestData.PlanId]!.AsObject();
        odd["Name"] = "单周";
        odd["TimeRule"]!["WeekCountDiv"] = 1;
        var even = odd.DeepClone().AsObject();
        even["Name"] = "双周";
        even["TimeRule"]!["WeekCountDiv"] = 2;
        plans[Guid.NewGuid().ToString()] = even;

        var result = ProfileDocument.BuildSchedule(ProfileDocument.Serialize(profile), Monday, days: 8);
        Assert.True(result.UsesWeekRotation);
        Assert.Equal("单周", result.Bundle.Days[0].ClassPlanName);
        Assert.Equal("双周", result.Bundle.Days[7].ClassPlanName);
    }

    [Fact]
    public async Task ScheduleApi_UsesProfileOnlyUntilPluginHasEverSynced()
    {
        await using var factory = new TestWebApplicationFactory();
        var classId = await CreateClassWithProfileAsync(factory, "档案课表班");
        var login = await factory.LoginAsync();
        using var client = factory.CreateClient();

        var fromProfile = await GetScheduleAsync(client, login.AccessToken, classId);
        Assert.Equal(ScheduleBundleSources.Profile, fromProfile.Source);
        Assert.Equal(7, fromProfile.Days.Count);

        // 插件上线过之后，即使离线且数据已过期，也继续显示最后一次同步的课表。
        var state = factory.Services.GetRequiredService<IStateStore>();
        state.SaveSchedule(classId, new ScheduleBundle
        {
            FromDate = "2020-01-01",
            ClassId = classId,
            Days = [new ScheduleDay { Date = "2020-01-01", Enabled = true, ClassPlanName = "插件课表" }],
        });
        var fromPlugin = await GetScheduleAsync(client, login.AccessToken, classId);
        Assert.Null(fromPlugin.Source);
        Assert.Equal("插件课表", Assert.Single(fromPlugin.Days).ClassPlanName);
    }

    [Fact]
    public async Task MySchedule_IncludesProfileClassesWithoutGrantingClassAccess()
    {
        await using var factory = new TestWebApplicationFactory();
        var classId = await CreateClassWithProfileAsync(factory, "档案日程班");
        var admin = await factory.LoginAsync();
        using var client = factory.CreateClient();
        // 档案科目“语文”的教师名为“原教师/张三”时，两位老师都能在我的日程中看到这门课。
        using (var scope = factory.Services.CreateScope())
        {
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var adminUser = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            var stored = (await library.ListAsync(adminUser, classId)).Single();
            var edited = ProfileDocument.Parse(stored.ProfileJson);
            edited["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"] = "原教师/张三";
            await library.SaveAsync(adminUser, [ProfileTestData.Edit(stored, stored.Name, ProfileDocument.Serialize(edited))], classId);
        }
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken, new CreateUserRequest
        {
            Username = "zhang.san",
            DisplayName = "张三",
            Password = "Teacher-Password-2026",
            RoleId = AccountRole.TeacherId,
        }))).EnsureSuccessStatusCode();
        var teacher = await factory.LoginAsync("zhang.san", "Teacher-Password-2026");

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule", teacher.AccessToken));
        response.EnsureSuccessStatusCode();
        var schedule = (await response.Content.ReadFromJsonAsync<MyScheduleResponse>())!;
        // 七天内必有一个周一，档案只排了周一的语文。
        var item = Assert.Single(Assert.Single(schedule.Days).Items);
        Assert.Equal(classId, item.ClassId);
        Assert.Equal(ScheduleBundleSources.Profile, item.Source);
        Assert.Equal("语文", Assert.Single(item.Courses).Subject);

        var classes = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/classes", teacher.AccessToken));
        classes.EnsureSuccessStatusCode();
        Assert.DoesNotContain((await classes.Content.ReadFromJsonAsync<List<ClassSummary>>())!, x => x.Id == classId);
    }

    private static async Task<Guid> CreateClassWithProfileAsync(TestWebApplicationFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var classId = (await scope.ServiceProvider.GetRequiredService<ClassroomService>().CreateAsync(name)).Id;
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>()
            .SaveAsync(admin, [ProfileTestData.New("档案课表", classId)]);
        return classId;
    }

    private static async Task<ScheduleBundle> GetScheduleAsync(HttpClient client, string token, Guid classId)
    {
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/schedule?classId={classId}", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScheduleBundle>())!;
    }
}
