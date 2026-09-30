using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// “老师”角色：内置角色种子、显示名与课表教师名的绑定、任教班级的默认权限与“我的日程”聚合。
/// </summary>
public sealed class TeacherBindingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TeacherBindingTests(TestWebApplicationFactory factory) => _factory = factory;

    // ---------- 姓名匹配（纯单元测试） ----------

    [Theory]
    [InlineData("王老师", "王老师", true)]
    [InlineData(" 王老师 ", "王老师", true)]
    [InlineData("王老师", "张三、王老师", true)]
    [InlineData("王老师", "张三/王老师", true)]
    [InlineData("王老师", "王老师傅", false)]
    [InlineData("王老师", "张三", false)]
    [InlineData("", "王老师", false)]
    [InlineData("王老师", " ", false)]
    [InlineData(null, "王老师", false)]
    [InlineData("王老师", null, false)]
    public void TeacherBinding_MatchesNameExactlyOrAsSplitToken(
        string? boundName, string? teacherField, bool expected)
    {
        Assert.Equal(expected, TeacherBindingService.Matches(boundName, teacherField));
    }

    // ---------- 内置角色种子 ----------

    [Fact]
    public async Task RolesApi_SeedsBuiltinTeacherRoleWithDefaultPermissions()
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/roles", admin.AccessToken));
        response.EnsureSuccessStatusCode();
        var roles = (await response.Content.ReadFromJsonAsync<List<AccountRoleInfo>>())!;
        var teacher = roles.Single(x => x.Id == AccountRole.TeacherId);

        Assert.Equal("老师", teacher.Name);
        Assert.Equal(AccountRoleKind.Teacher.ToString(), teacher.Kind);
        Assert.Equal(IdentityCoordinator.TeacherDefaultPermissions, teacher.DefaultPermissions);
        Assert.True(teacher.DefaultPermissions.HasFlag(UserPermissions.SendNotifications));
        Assert.True(teacher.DefaultPermissions.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.True(teacher.DefaultPermissions.HasFlag(UserPermissions.ChangeDisplayName));
        Assert.False(teacher.DefaultPermissions.HasFlag(UserPermissions.ManageUsers));
    }

    // ---------- 绑定与任教班级权限 ----------

    [Fact]
    public async Task Teacher_SeatsBoundClassInListScheduleAndCommandPermissions()
    {
        var (classId, subjectId) = await SeedTaughtClassAsync("绑定班");
        await CreateUserAsync("wang.teacher", "Teacher-Password-2026", AccountRole.TeacherId, displayName: "王老师");
        var teacher = await _factory.LoginAsync("wang.teacher", "Teacher-Password-2026");
        using var client = _factory.CreateClient();

        // 任教班级进入班级列表，默认权限只有通知与语音消息。
        var classesResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/classes", teacher.AccessToken));
        classesResponse.EnsureSuccessStatusCode();
        var classes = (await classesResponse.Content.ReadFromJsonAsync<List<ClassSummary>>())!;
        var bound = classes.Single(x => x.Id == classId);
        Assert.Equal("老师", bound.RoleName);
        var boundPermissions = bound.Permissions ?? UserPermissions.None;
        Assert.True(boundPermissions.HasFlag(UserPermissions.SendNotifications));
        Assert.True(boundPermissions.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.False(boundPermissions.HasFlag(UserPermissions.ManageSchedule));

        // 我的日程：按日期聚合出该班的匹配课程。
        var scheduleResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule", teacher.AccessToken));
        scheduleResponse.EnsureSuccessStatusCode();
        var schedule = (await scheduleResponse.Content.ReadFromJsonAsync<MyScheduleResponse>())!;
        var item = Assert.Single(schedule.Days.Single().Items);
        Assert.Equal(classId, item.ClassId);
        Assert.Equal("绑定班", item.ClassName);
        var course = Assert.Single(item.Courses);
        Assert.Equal(subjectId, course.SubjectId);
        Assert.Equal("王老师", course.Teacher);

        // 任教班级可以发通知：鉴权通过、命令进入下发路径，插件离线时返回 503 + PluginOffline 而不是 403。
        var taught = await SendNotificationAsync(client, teacher.AccessToken, classId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, taught.StatusCode);
        Assert.Equal(CommandResultCodes.PluginOffline, (await taught.Content.ReadFromJsonAsync<CommandResult>())!.Code);
        // 未任教且无成员关系的班级被 403 拒绝（默认班级例外：所有账号都是其成员，沿既有语义）。
        var other = await CreateClassAsync("未任教班");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendNotificationAsync(client, teacher.AccessToken, other.Id)).StatusCode);
    }

    [Fact]
    public async Task StudentRole_IsNotBoundByDisplayNameEvenWithSameName()
    {
        await SeedTaughtClassAsync("学生同名班");
        await CreateUserAsync("same.name.student", "Student-Password-2026", AccountRole.StudentId, displayName: "王老师");
        var student = await _factory.LoginAsync("same.name.student", "Student-Password-2026");
        using var client = _factory.CreateClient();

        var classesResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/classes", student.AccessToken));
        classesResponse.EnsureSuccessStatusCode();
        var classes = (await classesResponse.Content.ReadFromJsonAsync<List<ClassSummary>>())!;
        Assert.DoesNotContain(classes, x => x.Name == "学生同名班");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendNotificationAsync(client, student.AccessToken, await FindClassIdByNameAsync("学生同名班"))).StatusCode);
    }

    private async Task<ClassDetail> CreateClassAsync(string name)
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/classes", admin.AccessToken, new CreateClassRequest { Name = name }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClassDetail>())!;
    }

    [Fact]
    public async Task Teacher_CanUpdateDisplayNameAndBindingFollows()
    {
        var (classId, _) = await SeedTaughtClassAsync("改名班");
        await CreateUserAsync("rename.teacher", "Teacher-Password-2026", AccountRole.TeacherId, displayName: "占位名");
        var teacher = await _factory.LoginAsync("rename.teacher", "Teacher-Password-2026");
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/me/display-name", teacher.AccessToken,
            new ChangeDisplayNameRequest { DisplayName = "王老师" }))).StatusCode);

        var scheduleResponse = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me/schedule", teacher.AccessToken));
        scheduleResponse.EnsureSuccessStatusCode();
        var schedule = (await scheduleResponse.Content.ReadFromJsonAsync<MyScheduleResponse>())!;
        Assert.Contains(schedule.Days.SelectMany(x => x.Items), x => x.ClassId == classId);
    }

    [Fact]
    public async Task DisplayNameEndpoint_RequiresChangeDisplayNamePermission()
    {
        await CreateUserAsync("plain.rename", "Plain-User-Password-2026");
        var user = await _factory.LoginAsync("plain.rename", "Plain-User-Password-2026");
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/me/display-name", user.AccessToken,
            new ChangeDisplayNameRequest { DisplayName = "新名字" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- 授权镜像：任教老师即使不是班级成员也进入插件侧鉴权数据 ----------

    [Fact]
    public async Task CreateSyncAsync_IncludesTaughtTeachersWithTeacherPermissions()
    {
        var (classId, _) = await SeedTaughtClassAsync("镜像班");
        await CreateUserAsync("mirror.teacher", "Teacher-Password-2026", AccountRole.TeacherId, displayName: "王老师");

        using var scope = _factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var sync = await identities.CreateSyncAsync(classId);
        var account = sync.Accounts.Single(x => x.Username == "mirror.teacher");

        Assert.Equal("老师", account.RoleName);
        Assert.True(account.EffectivePermissions.HasFlag(UserPermissions.SendNotifications));
        Assert.True(account.EffectivePermissions.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.False(account.EffectivePermissions.HasFlag(UserPermissions.ManageUsers));
    }

    // ---------- 辅助 ----------

    /// <summary>创建一个新班级并向其 StateStore 缓存一份含“王老师”课程的课表。</summary>
    private async Task<(Guid ClassId, Guid SubjectId)> SeedTaughtClassAsync(string className)
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var created = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/classes", admin.AccessToken, new CreateClassRequest { Name = className }));
        created.EnsureSuccessStatusCode();
        var classId = (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;

        var subjectId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(classId, new ScheduleBundle
        {
            FromDate = "2026-09-29",
            ClassId = classId,
            Days =
            [
                new ScheduleDay
                {
                    Date = "2026-09-29",
                    Revision = "teacher-revision-1",
                    Enabled = true,
                    Courses =
                    [
                        new CourseEntry
                        {
                            Index = 0,
                            Label = "第一节",
                            SubjectId = subjectId,
                            Subject = "数学",
                            Teacher = "王老师",
                            StartTime = "08:00",
                            EndTime = "08:45",
                            Enabled = true,
                        },
                        new CourseEntry
                        {
                            Index = 1,
                            Label = "第二节",
                            SubjectId = Guid.NewGuid(),
                            Subject = "体育",
                            StartTime = "08:55",
                            EndTime = "09:40",
                            Enabled = true,
                        },
                    ],
                },
            ],
            Subjects = [new SubjectEntry { Id = subjectId, Name = "数学", Teacher = "王老师" }],
        });
        return (classId, subjectId);
    }

    private async Task<Guid> FindClassIdByNameAsync(string name)
    {
        var admin = await _factory.LoginAsync();
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/classes", admin.AccessToken));
        response.EnsureSuccessStatusCode();
        var classes = (await response.Content.ReadFromJsonAsync<List<ClassDetail>>())!;
        return classes.Single(x => x.Name == name).Id;
    }

    private async Task<HttpResponseMessage> SendNotificationAsync(HttpClient client, string token, Guid classId) =>
        await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, $"/api/commands?classId={classId}", token,
            new CommandMessage
            {
                Command = CommandKind.SendNotification,
                ClassId = classId,
                Notification = new NotificationRequest { Title = "标题", Message = "正文" },
            }));

    private async Task<Guid> CreateUserAsync(
        string username, string password, Guid? roleId = null, string? displayName = null)
    {
        var admin = await _factory.LoginAsync();
        var response = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest
            {
                Username = username,
                DisplayName = displayName ?? username,
                Password = password,
                RoleId = roleId,
            }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserListItem>())!.Id;
    }
}
