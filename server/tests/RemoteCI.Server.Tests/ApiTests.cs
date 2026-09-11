using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PluginPairingCode_IsOneTimeAndPluginOnly()
    {
        var badRole = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "watch",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var first = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var pair = await first.Content.ReadFromJsonAsync<PairResponse>();
        Assert.False(string.IsNullOrWhiteSpace(pair?.Token));

        var replay = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task PluginPairingCode_ExpiredCodeIsRejected()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pairing.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pairing = await database.PluginPairingCodes.SingleAsync();
            pairing.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });

        // 启用 ExpiresAt 后，过期配对码不能再签发插件凭证。
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PluginPairingCode_WebUiGeneratedCodeExpiresInThirtyMinutes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pairing-ttl.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var auth = await factory.LoginAsync();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/plugin/pairing-code", auth.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译；引导码不限时（MaxValue），只筛限时的新码。
        var created = (await database.PluginPairingCodes.ToListAsync())
            .Single(x => x.ExpiresAt < DateTimeOffset.MaxValue);
        var remaining = created.ExpiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public async Task Login_PersistsOnlyIdentityHashAndReturnsRotatableDeviceSession()
    {
        var wrong = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = TestWebApplicationFactory.AdminUsername,
            Password = "definitely-wrong",
            DeviceName = "Test",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var auth = await _factory.LoginAsync();
        Assert.Equal(UserPermissions.All, auth.User.Permissions);
        Assert.Equal(44, auth.AccessToken.Length);
        Assert.False(string.IsNullOrWhiteSpace(auth.DeviceSecret));

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshSessionRequest
        {
            DeviceSessionId = auth.DeviceSessionId,
            DeviceSecret = auth.DeviceSecret,
        });
        refresh.EnsureSuccessStatusCode();
        var rotated = (await refresh.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(auth.AccessToken, rotated.AccessToken);
        Assert.NotEqual(auth.DeviceSecret, rotated.DeviceSecret);

        await using var database = new FileStream(
            _factory.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await database.CopyToAsync(buffer);
        var databaseBytes = buffer.ToArray();
        Assert.DoesNotContain(TestWebApplicationFactory.AdminPassword, Encoding.UTF8.GetString(databaseBytes));
    }

    [Fact]
    public async Task Login_OnlyAcceptsId()
    {
        var admin = await _factory.LoginAsync();
        var first = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "login.id",
                DisplayName = "登录用户名",
                Password = "Login-Password-2026",
            }));
        first.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "login.id",
            Password = "Login-Password-2026",
            DeviceName = "ID login",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "登录用户名",
            Password = "Login-Password-2026",
            DeviceName = "Username login",
        })).StatusCode);
    }

    [Fact]
    public async Task PasswordChange_RevokesExistingCloudSessions()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "password.user",
                DisplayName = "改密测试",
                Password = "Original-Password-2026",
            }));
        create.EnsureSuccessStatusCode();
        var user = await LoginAsync("password.user", "Original-Password-2026");
        const string newPassword = "Changed-Password-2026";
        var changed = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/me/password",
            user.AccessToken,
            new ChangePasswordRequest
            {
                CurrentPassword = "Original-Password-2026",
                NewPassword = newPassword,
            }));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var oldToken = await _client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me", user.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        var oldPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "password.user",
            Password = "Original-Password-2026",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newLogin = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "password.user",
            Password = newPassword,
        });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task UserCanListAndRevokeOwnDeviceSession()
    {
        var auth = await _factory.LoginAsync();
        var socketClient = _factory.Server.CreateWebSocketClient();
        using var connectedWatch = await socketClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(auth.AccessToken)}"),
            CancellationToken.None);
        var sessionsResponse = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/me/sessions", auth.AccessToken));
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = (await sessionsResponse.Content.ReadFromJsonAsync<List<DeviceSessionSummary>>())!;
        Assert.Contains(sessions, x => x.Id == auth.DeviceSessionId && x.Current);

        var revoke = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/me/sessions/{auth.DeviceSessionId}", auth.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await AssertWebSocketClosedAsync(connectedWatch);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me", auth.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDeletedDisabledOrDemoted()
    {
        var admin = await _factory.LoginAsync();
        var demote = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.User,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var disable = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                Enabled = false,
            }));
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        var delete = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/users/{admin.User.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task CustomPermissions_AreEnforcedByEndpointsAndUpdateExistingToken()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "student.permissions",
                DisplayName = "权限测试学生",
                Password = "Student-Password-2026",
                Role = UserRole.User,
            }));
        create.EnsureSuccessStatusCode();
        var student = (await create.Content.ReadFromJsonAsync<UserListItem>())!;
        var login = await LoginAsync("student.permissions", "Student-Password-2026");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/users", login.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/admin/status", login.AccessToken))).StatusCode);

        var update = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{student.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = student.DisplayName,
                Role = UserRole.User,
                Enabled = true,
                GrantedPermissions = UserPermissions.AccessWebUi | UserPermissions.ManageUsers |
                    UserPermissions.SendNotifications | UserPermissions.ManageSchedule,
            }));
        update.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/users", login.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/admin/status", login.AccessToken))).StatusCode);

        var schedule = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/commands",
            login.AccessToken,
            new CommandMessage
            {
                Command = CommandKind.ChangeSchedule,
                ScheduleChange = new ScheduleChangeRequest { Date = "2026-08-11", ExpectedRevision = "x" },
        }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, schedule.StatusCode); // 已授权，但插件离线，不会排队。

        var delete = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/users/{student.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "student.permissions",
            Password = "Student-Password-2026",
        })).StatusCode);
    }

    [Fact]
    public async Task DatabaseRestart_PreservesAccountsAndPermissions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "restart.db");
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            var admin = await first.LoginAsync();
            var response = await first.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
                HttpMethod.Post,
                "/api/users",
                admin.AccessToken,
                new CreateUserRequest
                {
                    Username = "restart.user",
                    DisplayName = "重启用户",
                    Password = "Restart-Password-2026",
                    GrantedPermissions = UserPermissions.AccessWebUi,
                }));
            response.EnsureSuccessStatusCode();
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        var persisted = await second.LoginAsync("restart.user", "Restart-Password-2026");
        Assert.True(persisted.User.Permissions.HasFlag(UserPermissions.AccessWebUi));
    }

    [Fact]
    public async Task RazorLogin_PostWithoutCsrfToken_IsRejected()
    {
        var response = await _client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_AdminCanOpenEveryConsolePage()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        foreach (var path in new[] { "/", "/Account", "/Users", "/Schedule", "/Control", "/SystemConfig" })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
        var accountHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Account"));
        Assert.DoesNotContain("<h2>系统更新", accountHtml);
        var configHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/SystemConfig"));
        Assert.Contains("<span>系统配置</span>", configHtml);
        Assert.Contains("系统更新", configHtml);
        Assert.Contains("自动备份", configHtml);
        Assert.Contains("本地备份", configHtml);
        Assert.Contains("导出加密配置", configHtml);
        var usersHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Users"));
        Assert.Contains("角色配置", usersHtml);
        Assert.Contains("创建角色", usersHtml);
        Assert.Contains("""class="user-account-table role-summary-table""", usersHtml);
        Assert.Contains("""<dialog id="role-create-dialog""", usersHtml);
        var roleTableStart = usersHtml.IndexOf("""class="user-account-table role-summary-table""", StringComparison.Ordinal);
        var roleTableEnd = usersHtml.IndexOf("</table>", roleTableStart, StringComparison.Ordinal);
        Assert.True(roleTableStart >= 0 && roleTableEnd > roleTableStart);
        var roleTableHtml = usersHtml[roleTableStart..roleTableEnd];
        Assert.DoesNotContain("默认权限", roleTableHtml);
        Assert.DoesNotContain("<form", roleTableHtml);
        Assert.Contains("data-backup-settings-form", configHtml);
        Assert.Contains("data-backup-cadence", configHtml);
        Assert.Contains(">每小时</option>", configHtml);
        Assert.DoesNotContain("每小时整点", configHtml);
        Assert.Contains("data-backup-weekday hidden", configHtml);

        var oldNotifications = await browser.GetAsync("/Notifications");
        Assert.Equal(HttpStatusCode.Redirect, oldNotifications.StatusCode);
        Assert.Equal("/Control#send-notification", oldNotifications.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task RazorWebUi_ControlPageContainsActionsNotificationAndLiveVolume()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
            store.SaveSnapshot(new ClassStateSnapshot
            {
                IsNotificationPlaying = true,
                IsMainMenuVisible = false,
                IsSleepAvailable = true,
                IsHibernateAvailable = true,
                IsVolumeControlAvailable = true,
                VolumePercent = 42,
                IsMuted = false,
            });
            store.SaveExtensions(new[]
            {
                new ExtensionDefinition
                {
                    Id = "demo.lock",
                    DisplayName = "锁定教室",
                    RequiredPermission = UserPermissions.SystemControl,
                    Parameters =
                    [
                        new ExtensionParameter
                        {
                            Key = "reason",
                            Label = "原因",
                            Type = ExtensionParameterType.Text,
                            Required = true,
                        },
                    ],
                },
            });
        }

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));

        Assert.Contains("<span>控制</span>", html);
        Assert.Contains(@"href=""/Control""", html);
        Assert.DoesNotContain("nav-submenu", html);
        Assert.DoesNotContain(@"href=""/Notifications""", html);
        Assert.Contains(@"id=""send-notification""", html);
        Assert.Contains("发送并等待回执", html);
        Assert.Contains("老师来了", html);
        Assert.DoesNotContain("向插件发送单一指令，由插件显示强调提醒并在 1 秒后自动清除。", html);
        Assert.DoesNotContain("正文留空时显示原标题", html);
        Assert.Contains("清除当前提醒", html);
        Assert.Contains("显示主菜单", html);
        Assert.Matches(@"name=""visible""\s+value=""true""", html);
        Assert.Contains("当前音量 42%", html);
        Assert.Matches(@"name=""muted""\s+value=""true""", html);
        Assert.Contains("data-volume-form", html);
        Assert.Contains("data-volume-slider", html);
        Assert.DoesNotContain("设置音量", html);
        Assert.Contains("关机", html);
        Assert.Contains("重启", html);
        Assert.Contains("睡眠", html);
        Assert.Contains("休眠", html);
        Assert.Contains("锁定教室", html);
        Assert.Contains(@"name=""ExtensionInputs[0].Value""", html);
        Assert.Contains("扩展设置", html);
        Assert.Contains("启用扩展功能", html);
        Assert.Contains("对非管理员用户开放", html);
        Assert.Contains("在我自己的手表上展示", html);
        Assert.Contains("extension-control-row", html);
    }

    [Theory]
    [InlineData("", "正文")]
    [InlineData("标题", "")]
    [InlineData("", "")]
    public async Task RazorWebUi_NotificationAllowsEmptyTitleOrMessage(string title, string message)
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var controlHtml = await browser.GetStringAsync("/Control");

        var response = await PostRazorFormAsync(
            browser,
            "/Control?handler=Notification",
            controlHtml,
            new Dictionary<string, string>
            {
                ["Input.Title"] = title,
                ["Input.Message"] = message,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_SchedulePageExposesManualPullAndIntervalOptions()
    {
        var subjectId = Guid.NewGuid();
        using (var setupScope = _factory.Services.CreateScope())
        {
            setupScope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(new ScheduleBundle
            {
                FromDate = "2026-08-17",
                Days =
                [
                    new ScheduleDay
                    {
                        Date = "2026-08-17",
                        Revision = "revision-1",
                        ClassPlanName = "测试课表",
                        Enabled = true,
                        Courses =
                        [
                            new CourseEntry
                            {
                                Index = 0,
                                Label = "第一节",
                                SubjectId = subjectId,
                                Subject = "信息技术实践课程",
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
                Subjects = [new SubjectEntry { Id = subjectId, Name = "信息技术实践课程" }],
            });
        }

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var scheduleHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        Assert.Contains("立即拉取课表", scheduleHtml);
        Assert.Contains("class=\"schedule-pull-layout\"", scheduleHtml);
        Assert.Contains("class=\"schedule-pull-button\"", scheduleHtml);
        Assert.Contains("class=\"schedule-submit-button\"", scheduleHtml);
        Assert.Contains("<span>课表</span>", scheduleHtml);
        Assert.Contains("强制覆盖服务端缓存", scheduleHtml);
        Assert.Contains("data-schedule-pull-progress", scheduleHtml);
        Assert.Contains("""class="schedule-table""", scheduleHtml);
        Assert.Contains("""class="schedule-period-heading">节次""", scheduleHtml);
        Assert.Contains("<strong>周一</strong>", scheduleHtml);
        var scheduleTableStart = scheduleHtml.IndexOf("""<table class="schedule-table">""", StringComparison.Ordinal);
        var scheduleTableEnd = scheduleHtml.IndexOf("</table>", scheduleTableStart, StringComparison.Ordinal);
        Assert.True(scheduleTableStart >= 0 && scheduleTableEnd > scheduleTableStart);
        var scheduleTableHtml = scheduleHtml[scheduleTableStart..scheduleTableEnd];
        Assert.Single(Regex.Matches(scheduleTableHtml, "第一节").OfType<Match>());
        Assert.Contains("""class="schedule-course-name""", scheduleTableHtml);
        Assert.Contains("信息技术实践课程", scheduleTableHtml);
        Assert.Contains("08:00–08:45", scheduleTableHtml);
        Assert.Contains("每 15 分钟", scheduleHtml);
        Assert.Contains("每小时", scheduleHtml);
        Assert.Contains("每 6 小时", scheduleHtml);
        Assert.Contains("每天", scheduleHtml);
        Assert.Contains(">交换</option>", scheduleHtml);
        Assert.Contains(">替换</option>", scheduleHtml);
        Assert.DoesNotContain(">Exchange</option>", scheduleHtml);
        Assert.DoesNotContain(">Replace</option>", scheduleHtml);
        Assert.Contains("data-schedule-change-form", scheduleHtml);
        Assert.Contains("data-schedule-source", scheduleHtml);
        Assert.Contains("data-schedule-target", scheduleHtml);
        Assert.Contains("日期与目标节次", scheduleHtml);
        Assert.Contains("2026-08-17 · 第二节 · 体育", scheduleHtml);
        Assert.DoesNotContain("目标节次（从 0 开始）", scheduleHtml);
        Assert.Contains("name=\"Input.Date\"", scheduleHtml);
        Assert.Contains("name=\"Input.ExpectedRevision\"", scheduleHtml);
        Assert.Contains("name=\"Input.SourceIndex\"", scheduleHtml);
        Assert.Contains("name=\"Input.TargetIndex\"", scheduleHtml);
        Assert.Contains("data-date=\"2026-08-17\"", scheduleHtml);
        Assert.DoesNotMatch("value=\"[^\"]*\\|", scheduleHtml);
        Assert.Contains("data-exchange-field", scheduleHtml);
        Assert.Contains("data-replace-field hidden", scheduleHtml);
        Assert.Matches(@"data-replace-field hidden[\s\S]*?<select[^>]+disabled", scheduleHtml);

        var response = await PostRazorFormAsync(
            browser,
            "/Schedule?handler=PullInterval",
            scheduleHtml,
            new Dictionary<string, string> { ["PullInterval"] = "60" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SchedulePullSettings>();
        Assert.Equal(SchedulePullInterval.Hourly, await settings.GetIntervalAsync());

        var refreshedHtml = await browser.GetStringAsync("/Schedule");
        var invalidTarget = await PostRazorFormAsync(
            browser,
            "/Schedule",
            refreshedHtml,
            new Dictionary<string, string>
            {
                ["Input.Date"] = "2026-08-17",
                ["Input.ExpectedRevision"] = "revision-1",
                ["Input.SourceIndex"] = "0",
                ["Input.Mode"] = "Exchange",
                ["Input.TargetIndex"] = "2",
            });
        Assert.Equal(HttpStatusCode.Redirect, invalidTarget.StatusCode);
        var invalidTargetHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));
        Assert.Contains("请选择与原节次同一天的其他目标节次。", invalidTargetHtml);

        var validHtml = await browser.GetStringAsync("/Schedule");
        var validTarget = await PostRazorFormAsync(
            browser,
            "/Schedule",
            validHtml,
            new Dictionary<string, string>
            {
                ["Input.Date"] = "2026-08-17",
                ["Input.ExpectedRevision"] = "revision-1",
                ["Input.SourceIndex"] = "0",
                ["Input.Mode"] = "Exchange",
                ["Input.TargetIndex"] = "1",
            });
        Assert.Equal(HttpStatusCode.Redirect, validTarget.StatusCode);
        var validTargetHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));
        Assert.Contains("插件未在线，操作未执行", validTargetHtml);
    }

    [Fact]
    public async Task RazorWebUi_HidesInactiveSchedulePullProgress()
    {
        using var browser = CreateBrowserClient();
        var css = await browser.GetStringAsync("/app.css");

        Assert.Contains(".schedule-pull-progress[hidden]", css);
        Assert.Contains(".schedule-table { width: 100%; min-width: 1120px; table-layout: fixed", css);
        Assert.Contains(".schedule-period-heading, .schedule-period-cell { width: 90px; white-space: nowrap; }", css);
        Assert.Contains(".schedule-course-name, .schedule-course-time { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }", css);
        Assert.Contains(".schedule-pull-layout { display: grid; grid-template-columns: minmax(0,1fr) auto", css);
        Assert.Contains(".schedule-pull-button { width: auto; min-width: 240px; justify-self: end; }", css);
        Assert.Contains(".schedule-change-form .schedule-submit-button { grid-column: 3; width: 100%; }", css);
        Assert.Contains(".backup-list .row-actions { flex: 0 0 auto; align-items: center; flex-direction: row", css);
        var script = await browser.GetStringAsync("/app.js");
        Assert.Contains("[data-backup-settings-form]", script);
        Assert.Contains("weekdayField.hidden = !weekly", script);
        Assert.Contains("[data-schedule-target]", script);
        Assert.Contains("option.dataset.date === selectedSource?.dataset.date", script);
        Assert.DoesNotContain("source.value.split", script);
    }

    [Fact]
    public async Task RazorWebUi_ShowsGlobalScheduleTaskAndDisablesDuplicatePull()
    {
        using var scope = _factory.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<ScheduleSyncTaskTracker>();
        var running = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Automatic));
        try
        {
            using var browser = CreateBrowserClient();
            await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
            var scheduleHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

            Assert.Contains("已有课表任务正在执行", scheduleHtml);
            Assert.Contains("请勿重复操作", scheduleHtml);
            Assert.Contains("disabled", scheduleHtml);
        }
        finally
        {
            tracker.Observe(new ScheduleSyncStatus
            {
                TaskId = running.TaskId,
                Source = running.Source,
                State = ScheduleSyncTaskState.Failed,
                Message = "测试清理",
                StartedAt = running.StartedAt,
                FinishedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    [Fact]
    public async Task RazorWebUi_SystemConfigHostsUpdateActions()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var systemConfigHtml = await browser.GetStringAsync("/SystemConfig");
        var previousRuntime = Environment.GetEnvironmentVariable("REMOTECI_RUNTIME");

        try
        {
            // 让处理器走无需访问 GitHub 的 fnOS 分支，只验证真实 Razor 绑定与 ModelState 行为。
            Environment.SetEnvironmentVariable("REMOTECI_RUNTIME", "fnos");
            var response = await PostRazorFormAsync(browser, "/SystemConfig?handler=CheckUpdate", systemConfigHtml);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("由 fnOS 应用中心管理，请从 GitHub Releases 下载 FPK 手动升级。", responseHtml);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REMOTECI_RUNTIME", previousRuntime);
        }
    }

    [Fact]
    public async Task RazorWebUi_DevelopmentBuildDisablesSelfUpdate()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var response = await browser.GetAsync("/SystemConfig");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("系统配置", html);
        Assert.Contains(UpdateService.DevelopmentManagedMessage, html);
        Assert.DoesNotContain("强制更新（同版本重新下载并覆盖安装）", html);
    }

    [Fact]
    public async Task RazorWebUi_PasswordHandlerStillValidatesEmptyPasswordFields()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var accountHtml = await browser.GetStringAsync("/Account");

        var response = await PostRazorFormAsync(browser, "/Account?handler=Password", accountHtml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("<li>The CurrentPassword field is required.</li>", responseHtml);
        Assert.Contains("<li>The NewPassword field is required.</li>", responseHtml);
        Assert.Contains("<li>请再次输入新密码。</li>", responseHtml);
    }

    [Fact]
    public async Task RazorWebUi_PasswordHandlerRejectsMismatchedConfirmation()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var accountHtml = await browser.GetStringAsync("/Account");
        Assert.Contains("name=\"Password.NewPassword\"", accountHtml);
        Assert.Contains("name=\"Password.ConfirmNewPassword\"", accountHtml);
        Assert.Contains("data-password-confirmation", accountHtml);

        var response = await PostRazorFormAsync(
            browser,
            "/Account?handler=Password",
            accountHtml,
            new Dictionary<string, string>
            {
                ["Password.CurrentPassword"] = TestWebApplicationFactory.AdminPassword,
                ["Password.NewPassword"] = "Changed-Password-2026",
                ["Password.ConfirmNewPassword"] = "Different-Password-2026",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("<li>两次输入的新密码不一致。</li>", responseHtml);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
    }

    [Theory]
    [InlineData("非法 ID", "Valid-Password-2026", "Create.Username")]
    [InlineData("admin", "Valid-Password-2026", "Create.Username")]
    [InlineData("valid.id", "short", "Create.Password")]
    public async Task RazorWebUi_CreateUserClearsOnlyTheInvalidField(
        string username,
        string password,
        string expectedInvalidField)
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var usersHtml = await browser.GetStringAsync("/Users");
        var response = await PostRazorFormAsync(
            browser,
            "/Users?handler=Create",
            usersHtml,
            new Dictionary<string, string>
            {
                ["Create.Username"] = username,
                ["Create.DisplayName"] = "应保留的用户名",
                ["Create.Password"] = password,
                ["Create.Role"] = ((int)UserRole.Admin).ToString(),
                ["Create.AccessWebUi"] = "true",
                ["Create.ManageSchedule"] = "true",
            },
            ajax: true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var invalidFields = payload.RootElement.GetProperty("invalidFields").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.True(
            invalidFields.SequenceEqual([expectedInvalidField]),
            $"无效字段不匹配：{payload.RootElement}");
        Assert.False(payload.RootElement.TryGetProperty("password", out _));
        Assert.False(payload.RootElement.TryGetProperty("displayName", out _));
    }

    [Fact]
    public async Task RazorWebUi_UsersPageUsesSummaryRowsAndRoleAwareEditDialogs()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "dialog.student",
                DisplayName = "弹窗学生账号",
                Password = "Dialog-Student-Password-2026",
                GrantedPermissions = UserPermissions.AccessWebUi | UserPermissions.ManageSchedule,
            }));
        create.EnsureSuccessStatusCode();

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Users"));

        Assert.Contains("<table class=\"user-account-table\">", html);
        Assert.Contains("弹窗学生账号", html);
        Assert.Contains("<code>dialog.student</code>", html);
        Assert.Contains("data-user-edit-open=", html);
        Assert.Contains("data-role-select", html);
        Assert.Contains(">学生</option>", html);
        Assert.Contains(">管理员</option>", html);

        var tableStart = html.IndexOf("<table class=\"user-account-table\">", StringComparison.Ordinal);
        var tableEnd = html.IndexOf("</table>", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var tableHtml = html[tableStart..(tableEnd + "</table>".Length)];
        Assert.DoesNotContain("handler=Update", tableHtml);
        Assert.DoesNotContain("附加权限", tableHtml);
        Assert.DoesNotContain("重置密码", tableHtml);
        var studentRow = Regex.Match(tableHtml, @"<tr>[\s\S]*?<code>dialog\.student</code>[\s\S]*?</tr>");
        var adminRow = Regex.Match(tableHtml, @"<tr>[\s\S]*?<code>admin</code>[\s\S]*?</tr>");
        Assert.True(studentRow.Success);
        Assert.True(adminRow.Success);
        Assert.Contains("handler=Delete", studentRow.Value);
        Assert.Contains("删除", studentRow.Value);
        Assert.DoesNotContain("handler=Delete", adminRow.Value);

        var firstDialog = html.IndexOf("<dialog", tableEnd, StringComparison.Ordinal);
        Assert.True(firstDialog > tableEnd);
        Assert.True(html.IndexOf("重置密码", firstDialog, StringComparison.Ordinal) > firstDialog);

        var adminIdIndex = html.LastIndexOf("ID：admin", StringComparison.Ordinal);
        var adminDialogStart = html.LastIndexOf("<dialog", adminIdIndex, StringComparison.Ordinal);
        var adminDialogEnd = html.IndexOf("</dialog>", adminIdIndex, StringComparison.Ordinal);
        Assert.True(adminDialogStart >= 0 && adminDialogEnd > adminDialogStart);
        var adminDialog = html[adminDialogStart..(adminDialogEnd + "</dialog>".Length)];
        Assert.Contains("管理员默认拥有全部权限，附加权限不可修改。", adminDialog);
        Assert.Matches("data-role-permissions[^>]*\\shidden(?:=|\\s|>)", adminDialog);
        Assert.Matches("name=\\\"Edit.AccessWebUi\\\"[^>]*\\sdisabled(?:=|\\s|/|>)", adminDialog);

        var studentIdIndex = html.LastIndexOf("ID：dialog.student", StringComparison.Ordinal);
        var studentDialogStart = html.LastIndexOf("<dialog", studentIdIndex, StringComparison.Ordinal);
        var studentDialogEnd = html.IndexOf("</dialog>", studentIdIndex, StringComparison.Ordinal);
        Assert.True(studentDialogStart >= 0 && studentDialogEnd > studentDialogStart);
        var studentDialog = html[studentDialogStart..(studentDialogEnd + "</dialog>".Length)];
        Assert.Contains("data-role-permissions", studentDialog);
        Assert.DoesNotContain("handler=Delete", studentDialog);
        Assert.Contains("data-password-confirmation", studentDialog);
        Assert.Contains("name=\"confirmPassword\"", studentDialog);
        var studentPermission = Regex.Match(studentDialog, "<input[^>]*name=\\\"Edit.AccessWebUi\\\"[^>]*>");
        Assert.True(studentPermission.Success);
        Assert.DoesNotContain("disabled", studentPermission.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RazorWebUi_ResetPasswordRequiresMatchingConfirmation()
    {
        var admin = await _factory.LoginAsync();
        const string originalPassword = "Reset-Original-Password-2026";
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "reset.confirmation",
                DisplayName = "重置确认测试账号",
                Password = originalPassword,
            }));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<UserProfile>())!;

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var usersHtml = await browser.GetStringAsync("/Users");
        var response = await PostRazorFormAsync(
            browser,
            "/Users?handler=ResetPassword",
            usersHtml,
            new Dictionary<string, string>
            {
                ["id"] = created.Id.ToString(),
                ["password"] = "Reset-New-Password-2026",
                ["confirmPassword"] = "Reset-Different-Password-2026",
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var responseHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Users"));
        Assert.Contains("两次输入的新密码不一致。", responseHtml);
        var oldPasswordLogin = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = created.Username,
            Password = originalPassword,
        });
        Assert.Equal(HttpStatusCode.OK, oldPasswordLogin.StatusCode);

        const string newPassword = "Reset-New-Password-2026";
        var matchingHtml = await browser.GetStringAsync("/Users");
        var matchingResponse = await PostRazorFormAsync(
            browser,
            "/Users?handler=ResetPassword",
            matchingHtml,
            new Dictionary<string, string>
            {
                ["id"] = created.Id.ToString(),
                ["password"] = newPassword,
                ["confirmPassword"] = newPassword,
            });
        Assert.Equal(HttpStatusCode.Redirect, matchingResponse.StatusCode);
        var newPasswordLogin = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = created.Username,
            Password = newPassword,
        });
        Assert.Equal(HttpStatusCode.OK, newPasswordLogin.StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_PluginActionsLiveOnOverviewAndRetryReportsResult()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var usersHtml = await browser.GetStringAsync("/Users");
        Assert.DoesNotContain("生成配对码", usersHtml);
        Assert.Contains("<label>ID<input", usersHtml);
        Assert.Contains("<label>账号名<input", usersHtml);
        Assert.Contains("ID：admin", usersHtml);

        var overviewHtml = await browser.GetStringAsync("/");
        Assert.Contains("生成配对码", overviewHtml);
        Assert.Contains("重新检测连接", overviewHtml);
        Assert.DoesNotContain("去重试连接</a>", overviewHtml);

        var retry = await PostRazorFormAsync(browser, "/?handler=RetryConnection", overviewHtml);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal("/", retry.Headers.Location?.OriginalString);
        var retriedHtml = await browser.GetStringAsync("/");
        Assert.Contains("插件仍未连接", WebUtility.HtmlDecode(retriedHtml));

        overviewHtml = await browser.GetStringAsync("/");
        var pairCode = await PostRazorFormAsync(browser, "/?handler=PairCode", overviewHtml);
        Assert.Equal(HttpStatusCode.OK, pairCode.StatusCode);
        var pairCodeHtml = await pairCode.Content.ReadAsStringAsync();
        Assert.Contains("一次性插件配对码", pairCodeHtml);
        Assert.Contains("使用前持续有效", pairCodeHtml);
        Assert.Contains("data-copy-value", pairCodeHtml);
        Assert.Contains("bi-copy", pairCodeHtml);
    }

    [Fact]
    public async Task RazorWebUi_IncompatiblePluginProtocolShowsUpgradeGuidance()
    {
        await using var factory = new TestWebApplicationFactory();
        var pluginToken = await factory.GetPluginTokenAsync();
        var socketClient = factory.Server.CreateWebSocketClient();
        using var plugin = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None);
        var incompatible = Envelope.SchedulePull();
        incompatible.ProtocolVersion = 2;
        await SendEnvelopeAsync(plugin, incompatible);
        var error = await ReceivePayloadAsync<AuthState>(plugin, Protocol.MessageTypeAuthState);
        Assert.Equal(ApiErrorCodes.ProtocolVersionUnsupported, error.ErrorCode);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/"));

        Assert.Contains("协议不匹配", html);
        Assert.Contains("插件协议 v2", html);
        Assert.Contains("WebUI 协议 v3", html);
        Assert.Contains("请升级 ClassIsland 插件", html);
    }

    [Fact]
    public async Task RazorWebUi_CapabilityDiagnosticsShowChineseAnnotations()
    {
        await using var factory = new TestWebApplicationFactory();
        var pluginToken = await factory.GetPluginTokenAsync();
        var socketClient = factory.Server.CreateWebSocketClient();
        using var plugin = await socketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/"));

        Assert.Contains("class-state.read（读取课堂状态）", html);
        Assert.Contains("schedule.read（读取课表）", html);
        Assert.Contains("extensions.run（运行扩展功能）", html);
    }

    [Fact]
    public async Task TeacherComingPermission_IsIndependentFromNotificationPermission()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "teacher.alert",
                DisplayName = "老师来了权限测试",
                Password = "Teacher-Alert-Password-2026",
                GrantedPermissions = UserPermissions.AccessWebUi | UserPermissions.TeacherComing,
            }));
        create.EnsureSuccessStatusCode();
        var login = await LoginAsync("teacher.alert", "Teacher-Alert-Password-2026");

        var teacherComing = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/commands",
            login.AccessToken,
            new CommandMessage { Command = CommandKind.TeacherComing }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, teacherComing.StatusCode);

        var notification = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/commands",
            login.AccessToken,
            new CommandMessage
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest { Title = "x", Message = "x" },
            }));
        Assert.Equal(HttpStatusCode.Forbidden, notification.StatusCode);

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, "teacher.alert", "Teacher-Alert-Password-2026");
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));
        Assert.Contains("老师来了", html);
        Assert.DoesNotContain(@"id=""send-notification""", html);
        Assert.DoesNotContain("清除当前提醒", html);
    }

    [Fact]
    public async Task RazorWebUi_NonAdminCanEditOnlyOwnExtensionWatchVisibility()
    {
        var definition = new ExtensionDefinition
        {
            Id = "demo.personal.visibility",
            DisplayName = "个人展示扩展",
            RequiredPermission = UserPermissions.SendNotifications,
        };
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            var policies = scope.ServiceProvider.GetRequiredService<ExtensionPolicyService>();
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveExtensions([definition]);
            await policies.EnsureRegisteredAsync([definition]);
            var admin = (await identities.ListUsersAsync()).Single(x => x.Role == UserRole.Admin);
            await policies.UpdateAdminAsync(admin.Id, definition.Id, enabled: true, allowNonAdmin: true, showOnWatch: true);
            userId = (await identities.CreateUserAsync(new CreateUserRequest
            {
                Username = "extension.personal.web",
                DisplayName = "扩展个人设置",
                Password = "Extension-Personal-Web-2026",
                GrantedPermissions = UserPermissions.RunExtensions,
            })).Id;
        }

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, "extension.personal.web", "Extension-Personal-Web-2026");
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));
        Assert.Contains("个人展示扩展", html);
        Assert.Contains("执行", html);
        Assert.Contains("编辑", html);
        Assert.Contains("在我自己的手表上展示", html);
        Assert.DoesNotContain("启用扩展功能", html);
        Assert.DoesNotContain("对非管理员用户开放", html);
        Assert.DoesNotContain(@"id=""main-menu""", html);
        Assert.DoesNotContain(@"id=""volume""", html);
        Assert.DoesNotContain(@"id=""power""", html);

        var response = await PostRazorFormAsync(
            browser,
            "/Control?handler=ExtensionPreference",
            html,
            new Dictionary<string, string> { ["extensionId"] = definition.Id });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var verificationScope = _factory.Services.CreateScope();
        var profile = await verificationScope.ServiceProvider
            .GetRequiredService<IdentityCoordinator>().GetProfileAsync(userId);
        Assert.DoesNotContain(definition.Id, profile!.VisibleExtensionIds!);
    }

    [Fact]
    public async Task RazorWebUi_RoleDefaultsDriveControlNavigation()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var role = await scope.ServiceProvider.GetRequiredService<AccountRoleService>()
                .CreateAsync("Navigation default", UserPermissions.TeacherComing);
            await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().CreateUserAsync(new CreateUserRequest
            {
                Username = "role.navigation",
                DisplayName = "Role navigation",
                Password = "Role-Navigation-Password-2026",
                RoleId = role.Id,
            });
        }

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, "role.navigation", "Role-Navigation-Password-2026");
        var accountHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Account"));
        Assert.Contains("<span>控制</span>", accountHtml);
        var control = await browser.GetAsync("/Control");
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        Assert.Contains("老师来了", WebUtility.HtmlDecode(await control.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task RazorWebUi_OrdinaryUserOnlyGetsPersonalPages()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "web.student",
                DisplayName = "WebUI 普通用户",
                Password = "Web-Student-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, "web.student", "Web-Student-Password-2026");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
        var schedule = await browser.GetAsync("/Schedule");
        Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        var scheduleHtml = WebUtility.HtmlDecode(await schedule.Content.ReadAsStringAsync());
        Assert.Contains("未来七日课表", scheduleHtml);
        Assert.Contains("立即拉取课表", scheduleHtml);
        Assert.DoesNotContain("提交修改", scheduleHtml);
        Assert.DoesNotContain("自动拉取课表", scheduleHtml);
        var pull = await PostRazorFormAsync(
            browser,
            "/Schedule?handler=Pull",
            scheduleHtml,
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, pull.StatusCode);
        foreach (var path in new[] { "/", "/Users", "/Control", "/Notifications", "/SystemConfig" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Denied", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task Login_LocksOutAfterRepeatedFailuresButLeavesOtherAccountsAlone()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "lockout.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "lockout.user",
                DisplayName = "锁定测试",
                Password = "Lockout-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Username = "lockout.user",
                Password = "wrong-password",
                DeviceName = "Lockout Test",
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // 第 9 次即使密码正确也处于锁定状态（与 WebUI 登录行为一致）。
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "lockout.user",
            Password = "Lockout-Password-2026",
            DeviceName = "Lockout Test",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        // 其他账号不受影响。
        var other = await factory.LoginAsync();
        Assert.True(other.AccessToken.Length > 0);
    }

    [Fact]
    public async Task PluginPairingCode_ConcurrentConsumptionOnlySucceedsOnce()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pair-race.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);

        var requests = Enumerable.Range(0, 8).Select(_ => factory.CreateClient().PostAsJsonAsync(
            "/api/plugin/pair",
            new PairRequest { PairCode = TestWebApplicationFactory.TestPairCode, Role = "plugin" }));
        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task ManageUsersGrant_CannotCreateEditOrResetAdmins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "escalation.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "manager.user",
                DisplayName = "普通管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        create.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "manager.user", "Manager-Password-2026");

        // 不能创建管理员账号。
        var createAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            manager.AccessToken,
            new CreateUserRequest
            {
                Username = "sneaky.admin",
                DisplayName = "伪装管理员",
                Password = "Sneaky-Password-2026",
                Role = UserRole.Admin,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, createAdmin.StatusCode);

        // 不能把自己或他人升级为管理员。
        var promote = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{manager.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "普通管理者",
                Role = UserRole.Admin,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        // 不能编辑、重置密码或删除管理员账号。
        var editAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, editAdmin.StatusCode);
        var resetAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            $"/api/users/{admin.User.Id}/password",
            manager.AccessToken,
            new ResetPasswordRequest { Password = "Stolen-Password-2026" }));
        Assert.Equal(HttpStatusCode.Forbidden, resetAdmin.StatusCode);
        var deleteAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete,
            $"/api/users/{admin.User.Id}",
            manager.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, deleteAdmin.StatusCode);

        // 管理普通账号仍然可行（权限没有被误伤）。
        var editPeer = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{manager.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "普通管理者·改",
                Role = UserRole.User,
                Enabled = true,
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        Assert.Equal(HttpStatusCode.OK, editPeer.StatusCode);
    }

    [Fact]
    public async Task ManageUsersGrant_CannotTakeOverDisabledAdmins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "disabled-admin.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();

        // 第二个管理员被禁用后，守卫不得因 GetProfile 返回 null 而放行普通用户接管。
        var createAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "second.admin",
                DisplayName = "被禁用的管理员",
                Password = "Second-Admin-Password-2026",
                Role = UserRole.Admin,
            }));
        createAdmin.EnsureSuccessStatusCode();
        var secondAdmin = (await createAdmin.Content.ReadFromJsonAsync<UserListItem>())!;
        var disable = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{secondAdmin.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "被禁用的管理员",
                Role = UserRole.Admin,
                Enabled = false,
            }));
        disable.EnsureSuccessStatusCode();

        var createManager = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "manager.user",
                DisplayName = "普通管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        createManager.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "manager.user", "Manager-Password-2026");

        // 不能通过重新启用被禁用的管理员完成接管。
        var reenable = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{secondAdmin.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "被禁用的管理员",
                Role = UserRole.User,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, reenable.StatusCode);
        var resetDisabled = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            $"/api/users/{secondAdmin.Id}/password",
            manager.AccessToken,
            new ResetPasswordRequest { Password = "Stolen-Password-2026" }));
        Assert.Equal(HttpStatusCode.Forbidden, resetDisabled.StatusCode);
        var deleteDisabled = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete,
            $"/api/users/{secondAdmin.Id}",
            manager.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, deleteDisabled.StatusCode);
    }

    [Fact]
    public async Task PluginCredentials_ListedAndRevokedByAdminOnly_AndRevokedTokenStopsConnecting()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "credential.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var pluginToken = await factory.GetPluginTokenAsync();
        var admin = await factory.LoginAsync();

        var list = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", admin.AccessToken));
        list.EnsureSuccessStatusCode();
        var credentials = (await list.Content.ReadFromJsonAsync<List<PluginCredentialInfo>>())!;
        var credential = Assert.Single(credentials);
        Assert.True(credential.Enabled);

        var connectedSocketClient = factory.Server.CreateWebSocketClient();
        using var connectedPlugin = await connectedSocketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None);

        // 未登录与仅持有 ManageUsers 的普通用户均不可访问。
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/plugins/credentials")).StatusCode);
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "credential.manager",
                DisplayName = "凭证管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        create.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "credential.manager", "Manager-Password-2026");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", manager.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/plugins/credentials/{credential.Id}", manager.AccessToken))).StatusCode);

        // 管理员吊销后：列表标记禁用，被吊销令牌无法再建立 WebSocket。
        var revoke = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/plugins/credentials/{credential.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await AssertWebSocketClosedAsync(connectedPlugin);

        var after = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", admin.AccessToken));
        var afterList = (await after.Content.ReadFromJsonAsync<List<PluginCredentialInfo>>())!;
        Assert.False(afterList.Single(x => x.Id == credential.Id).Enabled);

        var webSocket = factory.Server.CreateWebSocketClient();
        // TestServer 握手失败抛 InvalidOperationException 而非 WebSocketException，按状态码断言。
        var connectError = await Assert.ThrowsAnyAsync<Exception>(() => webSocket.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None));
        Assert.Contains("401", connectError.Message);
    }

    [Fact]
    public async Task RazorWebUi_PluginCredentialsVisibleToAdminAndRevocable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "credential-ui.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var pluginToken = await factory.GetPluginTokenAsync(); // 制造一条插件凭证（名称“ClassIsland 插件”）。
        var connectedSocketClient = factory.Server.CreateWebSocketClient();
        using var connectedPlugin = await connectedSocketClient.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var overview = await browser.GetStringAsync("/");
        Assert.Contains("插件凭据", WebUtility.HtmlDecode(overview));
        Assert.Contains("ClassIsland 插件", WebUtility.HtmlDecode(overview));
        Assert.Contains("最近活跃", WebUtility.HtmlDecode(overview));

        Guid credentialId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            credentialId = (await database.PluginCredentials.SingleAsync()).Id;
        }
        var revoke = await PostRazorFormAsync(browser, $"/?handler=RevokeCredential&id={credentialId}", overview);
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        await AssertWebSocketClosedAsync(connectedPlugin);

        var afterHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/"));
        Assert.Contains("已吊销", afterHtml);

        // 普通用户看不到该节。
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "view.only",
                DisplayName = "只读用户",
                Password = "View-Only-Password-2026",
                GrantedPermissions = UserPermissions.AccessWebUi,
            }));
        create.EnsureSuccessStatusCode();
        using var userBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(userBrowser, "view.only", "View-Only-Password-2026");
        var userHtml = WebUtility.HtmlDecode(await userBrowser.GetStringAsync("/"));
        Assert.DoesNotContain("插件凭据", userHtml);
    }

    [Fact]
    public async Task BootstrapWithSecretLoggingDisabled_StillBootsAndBindsOption()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "quiet-bootstrap.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath, new Dictionary<string, string?>
        {
            ["Server:LogBootstrapSecrets"] = "false",
        });

        var options = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        Assert.False(options.LogBootstrapSecrets);

        // 关闭日志输出后初始管理员照常创建、可登录（行为不受开关影响）。
        var admin = await factory.LoginAsync();
        Assert.True(admin.AccessToken.Length > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VoicePermission_IsIndependentInRestAndWebUi(bool voiceGranted)
    {
        var username = voiceGranted ? "voice.only" : "notice.only";
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest
            {
                Username = username, DisplayName = "语音权限测试", Password = "Voice-Password-2026",
                GrantedPermissions = UserPermissions.AccessWebUi |
                    (voiceGranted ? UserPermissions.SendVoiceMessages : UserPermissions.SendNotifications),
            }));
        create.EnsureSuccessStatusCode();
        var auth = await LoginAsync(username, "Voice-Password-2026");
        var response = await _client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/commands", auth.AccessToken,
            new CommandMessage { Command = CommandKind.SendVoiceMessage, VoiceMessage = new() { AudioBase64 = "AAA=" } }));
        Assert.Equal(voiceGranted ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden, response.StatusCode);
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, username, "Voice-Password-2026");
        var html = await browser.GetStringAsync("/Control");
        Assert.Equal(voiceGranted, html.Contains("data-voice-form"));
        Assert.Equal(!voiceGranted, html.Contains("id=\"send-notification\""));
        // 二进制上传仍受 Razor 防伪保护，不能通过跨站请求触发教室播放。
        using var missingToken = new ByteArrayContent([0, 0]);
        missingToken.Headers.ContentType = new("application/octet-stream");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Control?handler=VoiceMessage", missingToken)).StatusCode);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/Control?handler=VoiceMessage") { Content = new ByteArrayContent([0, 0]) };
        upload.Headers.Add("X-CSRF-TOKEN", WebUtility.HtmlDecode(token));
        upload.Content.Headers.ContentType = new("application/octet-stream");
        var uploaded = await browser.SendAsync(upload);
        if (voiceGranted)
        {
            uploaded.EnsureSuccessStatusCode();
            Assert.Equal(CommandResultCodes.PluginOffline, (await uploaded.Content.ReadFromJsonAsync<CommandResult>())!.Code);
        }
        else Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
    }

    [Fact]
    public async Task VoicePermissionAppearsInUserAndRoleAssignmentForms()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var html = await browser.GetStringAsync("/Users");
        Assert.Contains("Create.SendVoiceMessages", html);
        Assert.Contains("Edit.SendVoiceMessages", html);
        Assert.Contains("RoleEdit.SendVoiceMessages", html);
        Assert.Equal(UserPermissions.SendVoiceMessages, new RemoteCI.Server.Pages.UsersModel.UserInput { SendVoiceMessages = true }.Grants);
    }

    private HttpClient CreateBrowserClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task LoginWebUiAsync(HttpClient browser, string username, string password)
    {
        var html = await browser.GetStringAsync("/Login");
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRazorFormAsync(
        HttpClient browser,
        string path,
        string html,
        IReadOnlyDictionary<string, string>? values = null,
        bool ajax = false)
    {
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Razor 表单页必须包含 CSRF 令牌");
        var fields = new Dictionary<string, string>(values ?? new Dictionary<string, string>())
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (ajax)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("application/json");
        }
        return await browser.SendAsync(request);
    }

    private async Task<AuthResponse> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            DeviceName = "Permission Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task AssertWebSocketClosedAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType != WebSocketMessageType.Close) continue;
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
            return;
        }
    }

    private static async Task SendEnvelopeAsync(WebSocket socket, Envelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<T> ReceivePayloadAsync<T>(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var envelope = JsonSerializer.Deserialize<Envelope>(
                Encoding.UTF8.GetString(buffer, 0, result.Count), JsonDefaults.Options)!;
            if (envelope.Type != expectedType) continue;
            return JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
        }
    }

    private static async Task<AuthResponse> LoginViaAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            DeviceName = "Isolated Factory Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
