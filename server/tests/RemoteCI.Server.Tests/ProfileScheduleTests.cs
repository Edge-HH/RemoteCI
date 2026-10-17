using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public void ClassClock_WithoutOwnSnapshotUsesAnyClassThenCallerOffset()
    {
        var state = new StateStore();
        var profileClass = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var phone = TimeSpan.FromHours(-5);

        // 还没有任何班级快照：用调用方（手机）上报的时区，而不是服务端时区（Docker 默认 UTC）。
        Assert.Equal(phone, ClassClock.Offset(state, profileClass, now, phone));

        // 其他班级的教室电脑上报过时区：按档案推算的班级沿用它；超出范围的偏移被忽略。
        state.SaveSnapshot(Guid.NewGuid(), new ClassStateSnapshot { TimeZoneOffsetMinutes = 600, GeneratedAt = now.AddMinutes(-5) });
        state.SaveSnapshot(Guid.NewGuid(), new ClassStateSnapshot { TimeZoneOffsetMinutes = 99999, GeneratedAt = now });
        Assert.Equal(TimeSpan.FromHours(10), ClassClock.Offset(state, profileClass, now, phone));

        // 本班自己的快照优先。
        state.SaveSnapshot(profileClass, new ClassStateSnapshot { TimeZoneOffsetMinutes = 480, GeneratedAt = now.AddDays(-1) });
        Assert.Equal(TimeSpan.FromHours(8), ClassClock.Offset(state, profileClass, now, phone));
    }

    [Fact]
    public async Task NextCourse_ProfileClassUsesCallerTimeZoneWhenNoClassroomReported()
    {
        await using var factory = new TestWebApplicationFactory();
        await CreateClassWithProfileAsync(factory, "档案时区班");
        var admin = await factory.LoginAsync();
        using var client = factory.CreateClient();
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken, new CreateUserRequest
        {
            Username = "profile.teacher",
            DisplayName = "原教师",
            Password = "Teacher-Password-2026",
            RoleId = AccountRole.TeacherId,
        }))).EnsureSuccessStatusCode();
        var teacher = await factory.LoginAsync("profile.teacher", "Teacher-Password-2026");

        // 档案只排了周一 08:00-08:40 的语文；手机所在时区为 UTC-5，测试环境里没有任何班级快照。
        var offset = TimeSpan.FromHours(-5);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(offset).DateTime);
        var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);
        var at = new DateTimeOffset(monday.ToDateTime(new TimeOnly(8, 10)), offset);
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get,
            $"/api/me/schedule/next?at={Uri.EscapeDataString(at.ToString("O"))}&timeZoneOffsetMinutes=-300", teacher.AccessToken));
        response.EnsureSuccessStatusCode();
        var next = (await response.Content.ReadFromJsonAsync<MyNextCourseResponse>())!;

        Assert.NotNull(next.Current);
        Assert.Equal("语文", next.Current!.Course.Subject);
        Assert.Equal(ScheduleBundleSources.Profile, next.Current.Source);
        Assert.Equal(new DateTimeOffset(monday.ToDateTime(new TimeOnly(8, 0)), offset), next.Current.StartsAt);

        var scheduleResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get,
            "/api/me/schedule?timeZoneOffsetMinutes=-300", teacher.AccessToken));
        scheduleResponse.EnsureSuccessStatusCode();
        var schedule = (await scheduleResponse.Content.ReadFromJsonAsync<MyScheduleResponse>())!;
        Assert.NotEmpty(schedule.Days);
        Assert.All(schedule.Days.SelectMany(day => day.Items), item => Assert.Equal(-300, item.TimeZoneOffsetMinutes));

        var invalid = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get,
            "/api/me/schedule/next?timeZoneOffsetMinutes=2000", teacher.AccessToken));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task WatchConnection_ReceivesProfileScheduleAndRefreshAfterSave()
    {
        await using var factory = new TestWebApplicationFactory();
        var classId = await CreateClassWithProfileAsync(factory, "档案推送班");
        var admin = await factory.LoginAsync();
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(admin.AccessToken)}"),
            CancellationToken.None);

        // 手机/手表只经 WebSocket 收课表：插件从未同步过的班级在连接时收到按档案推算的课表。
        var initial = await ReceiveScheduleAsync(socket, classId);
        Assert.Equal(ScheduleBundleSources.Profile, initial.Source);
        Assert.Contains(initial.Days.SelectMany(x => x.Courses), x => x.Subject == "语文");

        // 保存班级档案后立即推送新的推算结果。
        using (var scope = factory.Services.CreateScope())
        {
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var adminUser = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            var stored = (await library.ListAsync(adminUser, classId)).Single();
            var edited = ProfileDocument.Parse(stored.ProfileJson);
            edited["Subjects"]![ProfileTestData.SubjectId]!["Name"] = "数学";
            await library.SaveAsync(adminUser, [ProfileTestData.Edit(stored, stored.Name, ProfileDocument.Serialize(edited))], classId);
        }
        var updated = await ReceiveScheduleAsync(socket, classId);
        Assert.Equal(ScheduleBundleSources.Profile, updated.Source);
        Assert.Contains(updated.Days.SelectMany(x => x.Courses), x => x.Subject == "数学");
    }

    private static async Task<ScheduleBundle> ReceiveScheduleAsync(WebSocket socket, Guid classId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(message.ToArray()), JsonDefaults.Options)!;
            if (envelope.Type != Protocol.MessageTypeScheduleSync) continue;
            var bundle = JsonSerializer.Deserialize<ScheduleBundle>(JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            if (bundle.ClassId == classId) return bundle;
        }
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
