using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// 老师换课申请：申请/审批/拒绝、跨班互换与补偿、替换模式的临时任课老师、
/// 强制换课与撤回后的锁、审批人回退班主任，以及 Web Push 载荷加密。
/// 教室端由 FakeSchedulePlugin 代替：按命令直接修改服务端缓存的课表并返回新修订号。
/// </summary>
public sealed class ScheduleSwapTests : IDisposable
{
    private const string Password = "Teacher-Password-2026";
    private readonly TestWebApplicationFactory _root = new();
    private readonly FakeSchedulePlugin _plugin = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    private readonly string _tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1).ToString("yyyy-MM-dd");

    public ScheduleSwapTests()
    {
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IScheduleCommandSender>(sp =>
            {
                _plugin.Store = sp.GetRequiredService<IStateStore>();
                return _plugin;
            })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task SameClassExchange_RequestApprove_SwapsAndNotifiesBothSides()
    {
        var classId = await SeedClassAsync("一班", ("数学", "王老师"), ("英语", "李老师"));
        var wang = await CreateTeacherAsync("wang", "王老师");
        var li = await CreateTeacherAsync("li", "李老师");
        var wangToken = await TokenAsync("wang");
        var liToken = await TokenAsync("li");

        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(SwapRequestStatus.Pending, created.Status);
        Assert.Equal(["李老师"], created.ApproverNames);

        var liNotes = await GetAsync<List<UserNotificationView>>(liToken, "/api/me/notifications");
        Assert.Contains(liNotes, x => x.Kind == UserNotificationKinds.SwapRequested && x.SwapRequestId == created.Id);
        var incoming = await GetAsync<List<SwapRequestView>>(liToken, "/api/swap-requests?box=incoming");
        Assert.True(Assert.Single(incoming).CanDecide);

        // 申请人不能审批自己的申请。
        var self = await SendAsync(wangToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(HttpStatusCode.NotFound, self.StatusCode);

        var approved = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.ShortId}/approve", new SwapDecisionRequest { Note = "好的" });
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        var day = Day(classId, _today);
        Assert.Equal("英语", day.Courses[0].Subject);
        Assert.Equal("数学", day.Courses[1].Subject);
        Assert.Single(_plugin.Commands);
        Assert.Equal(ScheduleChangeMode.Exchange, _plugin.Commands[0].ScheduleChange!.Mode);
        Assert.False(_plugin.Commands[0].ScheduleChange!.Permanent);
        Assert.True(_plugin.Commands[0].RequestedBy!.Permissions.HasFlag(UserPermissions.ManageSchedule));

        var wangNotes = await GetAsync<List<UserNotificationView>>(wangToken, "/api/me/notifications");
        Assert.Contains(wangNotes, x => x.Kind == UserNotificationKinds.SwapApproved);
        Assert.NotEqual(Guid.Empty, wang);
        Assert.NotEqual(Guid.Empty, li);
    }

    [Fact]
    public async Task Create_RequiresOneOwnLesson_AndPermission()
    {
        var classId = await SeedClassAsync("二班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        var token = await TokenAsync("wang");

        var notOwn = await SendAsync(token, HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 1, classId, _today, 2));
        Assert.Equal(HttpStatusCode.BadRequest, notOwn.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapNotOwn, (await notOwn.Content.ReadFromJsonAsync<ApiError>())!.Code);

        await SetTeacherRolePermissionsAsync(remove: UserPermissions.RequestScheduleSwap);
        var denied = await SendAsync(token, HttpMethod.Post, "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // 没有强制换课权限时 force=true 被拒绝。
        await SetTeacherRolePermissionsAsync(add: UserPermissions.RequestScheduleSwap);
        var request = Exchange(classId, _today, 0, classId, _today, 1);
        request.Force = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/api/swap-requests", request)).StatusCode);
    }

    [Fact]
    public async Task ReplaceMode_InClassNotTaught_OverridesTeacherIntoPersonalSchedule()
    {
        await SeedClassAsync("三班", ("数学", "王老师"));
        // 四班的数学由张老师教，王老师替换四班第 1 节为自己的数学。
        var other = await SeedClassAsync("四班", ("英语", "李老师"), ("数学", "张老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var wangToken = await TokenAsync("wang");

        var created = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", new CreateSwapRequest
        {
            Mode = SwapMode.Replace,
            Target = new SwapSlot { ClassId = other, Date = _today, Index = 0 },
            SubjectName = "数学",
            Reason = "代课",
        });
        Assert.Equal(["李老师"], created.ApproverNames);
        await PostAsync<SwapRequestView>(await TokenAsync("li"), $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());

        var course = Day(other, _today).Courses[0];
        Assert.Equal("数学", course.Subject);
        Assert.Equal("王老师", course.Teacher);
        var schedule = await GetAsync<MyScheduleResponse>(wangToken, "/api/me/schedule");
        Assert.Contains(schedule.Days.SelectMany(x => x.Items), x => x.ClassId == other && x.Courses.Any(c => c.Index == 0));
    }

    [Fact]
    public async Task CrossClassExchange_SecondCommandFails_CompensatesFirstAndStaysPending()
    {
        var first = await SeedClassAsync("五班", ("数学", "王老师"), ("英语", "孙老师"));
        var second = await SeedClassAsync("六班", ("英语", "李老师"), ("数学", "王老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests",
            Exchange(first, _today, 0, second, _tomorrow, 0));

        _plugin.FailClass = second;
        var liToken = await TokenAsync("li");
        var failed = await SendAsync(liToken, HttpMethod.Post, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.False(failed.IsSuccessStatusCode);
        Assert.Equal("数学", Day(first, _today).Courses[0].Subject);
        Assert.Equal(3, _plugin.Commands.Count); // 五班替换、六班失败、五班补偿
        var pending = await GetAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}");
        Assert.Equal(SwapRequestStatus.Pending, pending.Status);

        _plugin.FailClass = null;
        var approved = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{created.Id}/approve", new SwapDecisionRequest());
        Assert.Equal(SwapRequestStatus.Approved, approved.Status);
        Assert.Equal("英语", Day(first, _today).Courses[0].Subject);
        Assert.Equal("李老师", Day(first, _today).Courses[0].Teacher);
        Assert.Equal("数学", Day(second, _tomorrow).Courses[0].Subject);
        Assert.Equal("王老师", Day(second, _tomorrow).Courses[0].Teacher);
    }

    [Fact]
    public async Task ForcedSwap_RevokeRestoresAndLocksOnlyThatRequester()
    {
        var classId = await SeedClassAsync("七班", ("数学", "王老师"), ("英语", "李老师"), ("物理", "赵老师"));
        await CreateTeacherAsync("wang", "王老师");
        await CreateTeacherAsync("li", "李老师");
        await CreateTeacherAsync("zhao", "赵老师");
        await SetTeacherRolePermissionsAsync(add: UserPermissions.ForceScheduleSwap);
        var wangToken = await TokenAsync("wang");
        var liToken = await TokenAsync("li");

        var forcedRequest = Exchange(classId, _today, 0, classId, _today, 1);
        forcedRequest.Force = true;
        var forced = await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", forcedRequest);
        Assert.Equal(SwapRequestStatus.Forced, forced.Status);
        Assert.Equal("英语", Day(classId, _today).Courses[0].Subject);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(liToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapForced);

        var revoked = await PostAsync<SwapRequestView>(liToken, $"/api/swap-requests/{forced.Id}/revoke", new { });
        Assert.Equal(SwapRequestStatus.Revoked, revoked.Status);
        Assert.Equal("数学", Day(classId, _today).Courses[0].Subject);
        Assert.Equal("英语", Day(classId, _today).Courses[1].Subject);
        Assert.Contains(await GetAsync<List<UserNotificationView>>(wangToken, "/api/me/notifications"),
            x => x.Kind == UserNotificationKinds.SwapRevoked);

        var again = await SendAsync(wangToken, HttpMethod.Post, "/api/swap-requests", forcedRequest);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ApiErrorCodes.SwapForceLocked, (await again.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // 普通申请不受锁影响；其他老师对同一节课的强制换课也不受影响。
        var normal = Exchange(classId, _today, 0, classId, _today, 1);
        Assert.Equal(SwapRequestStatus.Pending, (await PostAsync<SwapRequestView>(wangToken, "/api/swap-requests", normal)).Status);
        var zhaoForce = Exchange(classId, _today, 2, classId, _today, 1);
        zhaoForce.Force = true;
        Assert.Equal(SwapRequestStatus.Forced, (await PostAsync<SwapRequestView>(await TokenAsync("zhao"), "/api/swap-requests", zhaoForce)).Status);
    }

    [Fact]
    public async Task Approver_FallsBackToHomeroomTeacher_WhenCounterpartHasNoAccount()
    {
        var classId = await SeedClassAsync("八班", ("数学", "王老师"), ("音乐", "钱老师"));
        await CreateTeacherAsync("wang", "王老师");
        var homeroomId = await CreateUserAsync("banzhuren", "班主任甲", AccountRole.ClassAdministratorId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClassMemberships.Add(new ClassMembership { UserId = homeroomId, ClassroomId = classId, RoleDefinitionId = AccountRole.ClassAdministratorId });
            await db.SaveChangesAsync();
        }
        var created = await PostAsync<SwapRequestView>(await TokenAsync("wang"), "/api/swap-requests", Exchange(classId, _today, 0, classId, _today, 1));
        Assert.Equal(["班主任甲"], created.ApproverNames);

        var rejected = await PostAsync<SwapRequestView>(await TokenAsync("banzhuren"), $"/api/swap-requests/{created.Id}/reject", new SwapDecisionRequest { Note = "这节课有考试" });
        Assert.Equal(SwapRequestStatus.Rejected, rejected.Status);
        Assert.Empty(_plugin.Commands);
    }

    [Fact]
    public async Task BuiltinRoles_DefaultToRequestButNotForce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var roleId in new[] { AccountRole.TeacherId, AccountRole.ClassAdministratorId })
        {
            var role = await db.AccountRoles.SingleAsync(x => x.Id == roleId);
            Assert.True(role.DefaultPermissions.HasFlag(UserPermissions.RequestScheduleSwap));
            Assert.False(role.DefaultPermissions.HasFlag(UserPermissions.ForceScheduleSwap));
        }
    }

    [Fact]
    public void WebPush_EncryptsAccordingToRfc8291_AndSignsVapidJwt()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaParameters = userAgent.ExportParameters(false);
        var uaPublic = new byte[] { 0x04 }.Concat(uaParameters.Q.X!).Concat(uaParameters.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes("""{"title":"换课"}""");

        var body = WebPushSender.Encrypt(uaPublic, auth, payload);
        var salt = body[..16];
        var keyLength = body[20];
        var serverPublic = body[21..(21 + keyLength)];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var secret = userAgent.DeriveRawSecretAgreement(server.PublicKey);
        var (key, nonce) = WebPushSender.DeriveContentKeys(secret, auth, uaPublic, serverPublic, salt);
        var cipher = body[(21 + keyLength)..^16];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, body[^16..], plain);
        Assert.Equal(0x02, plain[^1]);
        Assert.Equal(payload, plain[..^1]);

        var keys = WebPushSender.GenerateKeys();
        var jwt = WebPushSender.CreateVapidJwt(keys, "https://push.example.com", DateTimeOffset.UtcNow.AddHours(1));
        var parts = jwt.Split('.');
        var publicKey = WebPushSender.Base64UrlDecode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            WebPushSender.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256));
    }

    // ---------- 辅助 ----------

    private CreateSwapRequest Exchange(Guid sourceClass, string sourceDate, int sourceIndex, Guid targetClass, string targetDate, int targetIndex) => new()
    {
        Mode = SwapMode.Exchange,
        Source = new SwapSlot { ClassId = sourceClass, Date = sourceDate, Index = sourceIndex },
        Target = new SwapSlot { ClassId = targetClass, Date = targetDate, Index = targetIndex },
        Reason = "外出教研",
    };

    /// <summary>新建班级并缓存今明两天相同的课表：第 i 节为 lessons[i] 的学科与老师。</summary>
    private async Task<Guid> SeedClassAsync(string name, params (string Subject, string Teacher)[] lessons)
    {
        var admin = await _root.LoginAsync();
        var created = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/classes", admin.AccessToken, new CreateClassRequest { Name = name }));
        created.EnsureSuccessStatusCode();
        var classId = (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;
        var subjects = lessons.Select(x => new SubjectEntry { Id = Guid.NewGuid(), Name = x.Subject, Teacher = x.Teacher }).ToList();
        ScheduleDay BuildDay(string date) => new()
        {
            Date = date,
            Revision = $"{date}-r0",
            Enabled = true,
            Courses = subjects.Select((subject, index) => new CourseEntry
            {
                Index = index,
                Label = $"第{index + 1}节",
                SubjectId = subject.Id,
                Subject = subject.Name,
                Teacher = subject.Teacher,
                StartTime = $"{8 + index:00}:00",
                EndTime = $"{8 + index:00}:45",
                Enabled = true,
            }).ToList(),
        };
        _factory.Services.GetRequiredService<IStateStore>().SaveSchedule(classId, new ScheduleBundle
        {
            FromDate = _today,
            ClassId = classId,
            Days = [BuildDay(_today), BuildDay(_tomorrow)],
            Subjects = subjects,
        });
        return classId;
    }

    private ScheduleDay Day(Guid classId, string date) =>
        _factory.Services.GetRequiredService<IStateStore>().GetLatestSchedule(classId)!.Days.Single(x => x.Date == date);

    private async Task SetTeacherRolePermissionsAsync(UserPermissions add = UserPermissions.None, UserPermissions remove = UserPermissions.None)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = await db.AccountRoles.SingleAsync(x => x.Id == AccountRole.TeacherId);
        role.DefaultPermissions = (role.DefaultPermissions | add) & ~remove;
        await db.SaveChangesAsync();
    }

    private Task<Guid> CreateTeacherAsync(string username, string displayName) =>
        CreateUserAsync(username, displayName, AccountRole.TeacherId);

    private async Task<Guid> CreateUserAsync(string username, string displayName, Guid roleId)
    {
        var admin = await _root.LoginAsync();
        var response = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest { Username = $"{username}.swap", DisplayName = displayName, Password = Password, RoleId = roleId }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserListItem>())!.Id;
    }

    private async Task<string> TokenAsync(string username)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = $"{username}.swap",
            Password = Password,
            DeviceName = "Swap Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    private Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, object? body = null) =>
        _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(method, path, token, body));

    private async Task<T> PostAsync<T>(string token, string path, object body)
    {
        var response = await SendAsync(token, HttpMethod.Post, path, body);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{path} 失败（{response.StatusCode}）：{await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(string token, string path)
    {
        var response = await SendAsync(token, HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>假教室端：校验修订号后直接改写服务端缓存课表，模拟插件换课并重推课表。</summary>
    private sealed class FakeSchedulePlugin : IScheduleCommandSender
    {
        private int _revision;
        public IStateStore Store { get; set; } = null!;
        public Guid? FailClass { get; set; }
        public ConcurrentQueue<CommandMessage> Received { get; } = new();
        public List<CommandMessage> Commands => [.. Received];

        public Task<CommandResult> SendAsync(Guid classId, CommandMessage command, TimeSpan timeout, CancellationToken ct)
        {
            Received.Enqueue(command);
            if (FailClass == classId)
                return Task.FromResult(CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行"));
            var change = command.ScheduleChange!;
            var bundle = Store.GetSourceSchedule(classId)!;
            var day = bundle.Days.Single(x => x.Date == change.Date);
            if (day.Revision != change.ExpectedRevision)
                return Task.FromResult(new CommandResult { Success = false, Code = CommandResultCodes.ScheduleStale, Message = "stale" });
            var courses = day.Courses.Select(Clone).ToList();
            var source = courses.Single(x => x.Index == change.SourceIndex);
            if (change.Mode == ScheduleChangeMode.Exchange)
            {
                var target = courses.Single(x => x.Index == change.TargetIndex);
                (source.SubjectId, target.SubjectId) = (target.SubjectId, source.SubjectId);
                (source.Subject, target.Subject) = (target.Subject, source.Subject);
                (source.Teacher, target.Teacher) = (target.Teacher, source.Teacher);
            }
            else
            {
                var subject = bundle.Subjects.Single(x => x.Id == change.ReplacementSubjectId);
                source.SubjectId = subject.Id;
                source.Subject = subject.Name;
                source.Teacher = subject.Teacher;
            }
            var revision = $"{change.Date}-r{Interlocked.Increment(ref _revision)}";
            var days = bundle.Days.Select(x => x.Date == change.Date
                ? new ScheduleDay { Date = x.Date, Revision = revision, Enabled = true, Courses = courses }
                : x).ToList();
            Store.SaveSchedule(classId, new ScheduleBundle
            {
                FromDate = bundle.FromDate,
                ClassId = classId,
                Days = days,
                Subjects = bundle.Subjects,
            });
            return Task.FromResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok, ScheduleRevision = revision });
        }

        private static CourseEntry Clone(CourseEntry x) => new()
        {
            Index = x.Index, Label = x.Label, SubjectId = x.SubjectId, Subject = x.Subject,
            StartTime = x.StartTime, EndTime = x.EndTime, Teacher = x.Teacher, Enabled = x.Enabled,
        };
    }
}
