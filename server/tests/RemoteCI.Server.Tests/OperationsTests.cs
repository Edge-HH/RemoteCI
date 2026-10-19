using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>集控运维功能：操作记录的可见范围与摘要脱敏。</summary>
public sealed class OperationsTests
{
    private const string ClassAdminPassword = "Ops-Class-Admin-2026";

    [Fact]
    public async Task AuditApi_ClassHeadSeesOwnClassesOnly_AdminSeesAll_OthersForbidden()
    {
        await using var factory = new TestWebApplicationFactory();
        var (admin, ownClass, classHead) = await CreateClassWithClassAdminAsync(factory, "审计一班", "audit.head");
        using var client = factory.CreateClient();
        var otherClass = await CreateClassAsync(client, admin, "审计二班");
        await SeedAuditAsync(factory, ownClass, "本班记录");
        await SeedAuditAsync(factory, otherClass, "他班记录");
        await SeedAuditAsync(factory, null, "未分配设备记录");

        var headPage = await GetAuditAsync(client, classHead);
        Assert.Contains(headPage.Items, x => x.Summary == "本班记录");
        Assert.DoesNotContain(headPage.Items, x => x.Summary is "他班记录" or "未分配设备记录");

        var adminPage = await GetAuditAsync(client, admin);
        Assert.Contains(adminPage.Items, x => x.Summary == "他班记录");
        Assert.Contains(adminPage.Items, x => x.Summary == "未分配设备记录");

        var filtered = await GetAuditAsync(client, admin, $"?classId={otherClass}");
        Assert.All(filtered.Items, x => Assert.Equal(otherClass, x.ClassId));

        var student = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin, new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "audit.student",
            DisplayName = "audit.student",
            Password = ClassAdminPassword,
            RoleId = AccountRole.StudentId,
            GrantedPermissions = UserPermissions.ApiAccess,
        }));
        student.EnsureSuccessStatusCode();
        var studentToken = (await factory.LoginAsync("audit.student", ClassAdminPassword)).AccessToken;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/audit", studentToken))).StatusCode);
    }

    [Fact]
    public async Task AuditPaging_KeepsSameMillisecondRecordsTogetherAcrossPages()
    {
        await using var factory = new TestWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 批量控制同一毫秒写入 3 条，再加 2 条更早的记录；每页 2 条时同毫秒的那组不能被截断。
            long[] times = [5000, 5000, 5000, 4000, 3000];
            db.CommandAuditEntries.AddRange(times.Select((at, i) => new CommandAuditEntry
            {
                Id = Guid.NewGuid(),
                MessageId = Guid.NewGuid().ToString("N"),
                CreatedAtUnixMs = at,
                ActorName = "测试",
                Command = (int)CommandKind.ClearNotifications,
                Summary = $"记录{i}",
            }));
            await db.SaveChangesAsync();
        }

        using var query = factory.Services.CreateScope();
        var audit = query.ServiceProvider.GetRequiredService<CommandAuditQueryService>();
        var seen = new List<string>();
        long? before = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await audit.QueryAsync(null, new CommandAuditFilter(Before: before, Limit: 2));
            seen.AddRange(result.Items.Select(x => x.Summary));
            if (result.NextBefore is not { } next) break;
            before = next;
        }
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void AuditSummary_KeepsReadableDetailsWithoutPayloads()
    {
        var audio = Convert.ToBase64String(new byte[VoiceMessageRequest.SampleRate * 2 * 3]);
        var voice = CommandAuditSummary.Describe(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage,
            VoiceMessage = new VoiceMessageRequest { AudioBase64 = audio, ForceComplete = true },
        });
        Assert.Equal("语音消息：约 3 秒，强制播放完成", voice);

        var file = CommandAuditSummary.Describe(new CommandMessage
        {
            Command = CommandKind.SendFile,
            FileDistribution = new FileDistributionRequest
            {
                FileName = "通知.docx",
                ContentBase64 = Convert.ToBase64String(new byte[2048]),
                TargetFolder = FileTargetFolder.Documents,
            },
        });
        Assert.Equal("分发文件：通知.docx，2 KB，文档", file);

        var terminal = CommandAuditSummary.Describe(new CommandMessage
        {
            Command = CommandKind.ExecuteTerminalCommand,
            TerminalCommand = new TerminalCommandRequest { Command = new string('x', 4000) },
        });
        Assert.True(terminal.Length <= CommandAuditLimits.TextLength);
        Assert.StartsWith("远程终端：xxx", terminal);
    }

    [Fact]
    public async Task DeviceAlert_FiresOnceDuringSchoolHours_AndSendsRecoveryWhenBackOnline()
    {
        await using var factory = new TestWebApplicationFactory();
        var classId = TestWebApplicationFactory.DefaultClassId;
        var token = await factory.GetPluginTokenAsync();
        // 教室端 UTC+8，当天 08:00-11:30 有课：在校时段为 07:30-11:30。
        var tenAm = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(8));
        SeedSchoolDay(factory, classId, tenAm);

        await EvaluateAlertsAsync(factory, tenAm);
        Assert.Empty(await NotificationsAsync(factory, UserNotificationKinds.DeviceOffline));

        await EvaluateAlertsAsync(factory, tenAm.AddMinutes(11));
        var alert = Assert.Single(await NotificationsAsync(factory, UserNotificationKinds.DeviceOffline));
        Assert.Equal($"{TestWebApplicationFactory.DefaultClassName} 教室电脑离线", alert.Title);
        Assert.Equal("/Devices", alert.Link);
        Assert.Equal(classId, alert.ClassId);

        await EvaluateAlertsAsync(factory, tenAm.AddMinutes(30));
        Assert.Single(await NotificationsAsync(factory, UserNotificationKinds.DeviceOffline));

        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"), CancellationToken.None);
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!peers.HasPluginFor(classId)) await Task.Delay(20, timeout.Token);
        await EvaluateAlertsAsync(factory, tenAm.AddMinutes(35));
        var recovered = Assert.Single(await NotificationsAsync(factory, UserNotificationKinds.DeviceOnline));
        Assert.Contains("已重新连接", recovered.Body);
        socket.Abort();
    }

    [Fact]
    public async Task DeviceAlert_StaysQuietOutsideSchoolHours()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.GetPluginTokenAsync();
        var evening = new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.FromHours(8));
        SeedSchoolDay(factory, TestWebApplicationFactory.DefaultClassId, evening);

        await EvaluateAlertsAsync(factory, evening);
        await EvaluateAlertsAsync(factory, evening.AddHours(2));

        Assert.Empty(await NotificationsAsync(factory, UserNotificationKinds.DeviceOffline));
    }

    [Fact]
    public async Task DeviceHealthApi_ListsAssignedDevicesForAdmins()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.GetPluginTokenAsync();
        var admin = (await factory.LoginAsync()).AccessToken;
        using var client = factory.CreateClient();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/devices/health", admin));
        response.EnsureSuccessStatusCode();
        var devices = (await response.Content.ReadFromJsonAsync<List<DeviceHealthItem>>(JsonDefaults.Options))!;

        var device = Assert.Single(devices);
        Assert.Equal(TestWebApplicationFactory.DefaultClassId, device.ClassId);
        Assert.False(device.Online);
    }

    [Fact]
    public async Task ClassBoard_ShowsCurrentLessonAnonymously_UntilRevoked()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        var classId = TestWebApplicationFactory.DefaultClassId;
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var state = factory.Services.GetRequiredService<IStateStore>();
        state.SaveSnapshot(classId, new ClassStateSnapshot { ClassId = classId, TimeZoneOffsetMinutes = 480 });
        var date = now.ToString("yyyy-MM-dd");
        state.SaveSchedule(classId, new ScheduleBundle
        {
            ClassId = classId,
            FromDate = date,
            Days = [new ScheduleDay
            {
                Date = date,
                Enabled = true,
                Courses = [new CourseEntry
                {
                    Index = 0, Label = "第1节", Subject = "班牌测试课", Teacher = "周老师", Enabled = true,
                    StartTime = now.AddMinutes(-10).ToString("HH:mm"), EndTime = now.AddMinutes(30).ToString("HH:mm"),
                }],
            }],
        });
        ClassBoardCreated created;
        using (var scope = factory.Services.CreateScope())
        {
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            created = await scope.ServiceProvider.GetRequiredService<ClassBoardService>()
                .CreateAsync(admin, classId, "门口班牌", showTeachers: false, "http://localhost");
        }
        var path = new Uri(created.Url).AbsolutePath;
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = WebUtility.HtmlDecode(await anonymous.GetStringAsync(path));
        Assert.Contains(TestWebApplicationFactory.DefaultClassName, html);
        Assert.Contains("班牌测试课", html);
        Assert.DoesNotContain("周老师", html);
        Assert.Contains("班牌测试课", WebUtility.HtmlDecode(await anonymous.GetStringAsync(path + "?handler=Content")));

        using (var scope = factory.Services.CreateScope())
        {
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            await scope.ServiceProvider.GetRequiredService<ClassBoardService>().RevokeAsync(admin, created.Board.Id);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(path)).StatusCode);
    }

    private static void SeedSchoolDay(TestWebApplicationFactory factory, Guid classId, DateTimeOffset localNow)
    {
        var state = factory.Services.GetRequiredService<IStateStore>();
        state.SaveSnapshot(classId, new ClassStateSnapshot { ClassId = classId, TimeZoneOffsetMinutes = 480, GeneratedAt = localNow });
        var date = localNow.ToString("yyyy-MM-dd");
        state.SaveSchedule(classId, new ScheduleBundle
        {
            ClassId = classId,
            FromDate = date,
            Days =
            [
                new ScheduleDay
                {
                    Date = date,
                    Enabled = true,
                    Courses =
                    [
                        new CourseEntry { Index = 0, Label = "第1节", Subject = "语文", StartTime = "08:00", EndTime = "08:45", Enabled = true },
                        new CourseEntry { Index = 1, Label = "第2节", Subject = "数学", StartTime = "10:45", EndTime = "11:30", Enabled = true },
                    ],
                },
            ],
        });
    }

    private static async Task EvaluateAlertsAsync(TestWebApplicationFactory factory, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeviceAlertEvaluator>().EvaluateAsync(now.ToUniversalTime());
    }

    private static async Task<List<UserNotification>> NotificationsAsync(TestWebApplicationFactory factory, string kind)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UserNotifications.AsNoTracking().Where(x => x.Kind == kind).ToListAsync();
    }

    private static async Task SeedAuditAsync(TestWebApplicationFactory factory, Guid? classId, string summary)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CommandAuditEntries.Add(new CommandAuditEntry
        {
            Id = Guid.NewGuid(),
            MessageId = Guid.NewGuid().ToString("N"),
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ActorName = "测试",
            Source = CommandAuditSource.WebUi,
            ClassId = classId,
            Command = (int)CommandKind.ClearNotifications,
            Summary = summary,
            Success = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<CommandAuditPage> GetAuditAsync(HttpClient client, string token, string query = "")
    {
        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/audit" + query, token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommandAuditPage>(JsonDefaults.Options))!;
    }

    private static async Task<Guid> CreateClassAsync(HttpClient client, string admin, string name)
    {
        var created = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/classes", admin,
            new CreateClassRequest { Name = name }));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;
    }

    /// <summary>创建班级并把新账号以班主任身份加入，返回管理员令牌、班级 Id 与班主任令牌。</summary>
    internal static async Task<(string Admin, Guid ClassId, string ClassAdmin)> CreateClassWithClassAdminAsync(
        TestWebApplicationFactory factory, string className, string username)
    {
        var admin = (await factory.LoginAsync()).AccessToken;
        using var client = factory.CreateClient();
        var classId = await CreateClassAsync(client, admin, className);
        // 新账号不加入默认班级，避免它同时成为默认班级的班主任而干扰可见范围断言。
        var user = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin, new CreateUserRequest
        {
            ClassId = classId,
            Username = username,
            DisplayName = username,
            Password = ClassAdminPassword,
            RoleId = AccountRole.ClassAdministratorId,
        }));
        user.EnsureSuccessStatusCode();
        var userId = (await user.Content.ReadFromJsonAsync<UserListItem>())!.Id;
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, $"/api/classes/{classId}/members", admin,
            new UpdateClassMembersRequest
            {
                Members = [new ClassMemberInput { UserId = userId, RoleId = AccountRole.ClassAdministratorId }],
            }))).EnsureSuccessStatusCode();
        return (admin, classId, (await factory.LoginAsync(username, ClassAdminPassword)).AccessToken);
    }
}
