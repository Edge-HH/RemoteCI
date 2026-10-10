using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;
using static RemoteCI.Server.Tests.TestWebApplicationFactory;

namespace RemoteCI.Server.Tests;

/// <summary>首次部署初始化向导、系统管理员保护，以及去掉默认班级后的成员关系与升级行为。</summary>
public sealed class SetupAndOwnerTests
{
    private const string OwnerPassword = "Owner-Password-2026";

    [Fact]
    public async Task FreshInstall_HasNoDefaultClassAndCreatesSystemOwnerOnce()
    {
        await using var factory = ForFreshInstall();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var status = await client.GetFromJsonAsync<SetupStatus>("/api/setup");
        Assert.True(status!.NeedsSystemAdmin);
        Assert.True(status.NeedsFirstClass);
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Classrooms.AnyAsync());

        // 登录页在还没有账号时转到初始化向导。
        var login = await client.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/Setup", login.Headers.Location!.OriginalString);

        var created = await client.PostAsJsonAsync("/api/setup/system-admin", new SetupSystemAdminRequest
        {
            Username = "owner", DisplayName = "部署所有者", Password = OwnerPassword,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var auth = (await created.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(UserRole.Admin, auth.User.Role);

        // 只能创建一次。
        var again = await client.PostAsJsonAsync("/api/setup/system-admin", new SetupSystemAdminRequest
        {
            Username = "intruder", Password = "Intruder-Password-2026",
        });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        var users = await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/users", auth.AccessToken)))
            .Content.ReadFromJsonAsync<List<UserListItem>>();
        Assert.True(Assert.Single(users!).IsSystemOwner);
        status = await client.GetFromJsonAsync<SetupStatus>("/api/setup");
        Assert.False(status!.NeedsSystemAdmin);
        Assert.True(status.NeedsFirstClass);
    }

    [Fact]
    public async Task FreshInstall_BootstrapPairCodeCreatesUnassignedDevice()
    {
        await using var factory = ForFreshInstall();
        using var client = factory.CreateClient();
        var paired = await client.PostAsJsonAsync("/api/plugin/pair", new PairRequest { PairCode = TestPairCode, Role = "plugin" });
        paired.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var credential = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PluginCredentials.SingleAsync();
        Assert.False(credential.Assigned);
        Assert.Null(credential.ClassroomId);
    }

    [Fact]
    public async Task WebSetupWizard_CreatesOwnerThenFirstClass()
    {
        await using var factory = ForFreshInstall();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var page = await browser.GetStringAsync("/Setup");
        Assert.Contains("创建系统管理员", WebUtility.HtmlDecode(page));
        var adminPost = await browser.PostAsync("/Setup?handler=Admin", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Admin.Username"] = "owner",
            ["Admin.DisplayName"] = "部署所有者",
            ["Admin.Password"] = OwnerPassword,
            ["Admin.ConfirmPassword"] = OwnerPassword,
            ["__RequestVerificationToken"] = Csrf(page),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, adminPost.StatusCode);

        // 已登录的系统管理员进入总览时会先被送回向导新建第一个班级。
        var index = await browser.GetAsync("/Index");
        Assert.Equal(HttpStatusCode.Redirect, index.StatusCode);
        Assert.StartsWith("/Setup", index.Headers.Location!.OriginalString);
        page = await browser.GetStringAsync("/Setup");
        Assert.Contains("新建第一个班级", WebUtility.HtmlDecode(page));
        var classPost = await browser.PostAsync("/Setup?handler=Class", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ClassName"] = "高一（1）班",
            ["__RequestVerificationToken"] = Csrf(page),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, classPost.StatusCode);
        Assert.Equal("/Index", classPost.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Index")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("高一（1）班", (await db.Classrooms.SingleAsync()).Name);
        Assert.True((await db.Users.SingleAsync()).IsSystemOwner);
    }

    [Fact]
    public async Task SystemOwner_CanOnlyBeManagedByItself()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var client = factory.CreateClient();
        var second = await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", owner.AccessToken, new CreateUserRequest
        {
            Username = "second.admin", DisplayName = "第二管理员", Password = "Second-Admin-2026", RoleId = AccountRole.AdministratorId,
        }));
        second.EnsureSuccessStatusCode();
        var otherAdmin = await factory.LoginAsync("second.admin", "Second-Admin-2026");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Put, $"/api/users/{owner.User.Id}", otherAdmin.AccessToken,
            new UpdateUserRequest { DisplayName = "被改名", Role = UserRole.Admin, RoleId = AccountRole.AdministratorId, Enabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Post, $"/api/users/{owner.User.Id}/password", otherAdmin.AccessToken,
            new ResetPasswordRequest { Password = "Taken-Over-2026" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/users/{owner.User.Id}", otherAdmin.AccessToken))).StatusCode);

        // 系统管理员本人可以改自己的显示名，原密码仍然有效。
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Put, $"/api/users/{owner.User.Id}", owner.AccessToken,
            new UpdateUserRequest { DisplayName = "部署所有者", Role = UserRole.Admin, RoleId = AccountRole.AdministratorId, Enabled = true }))).StatusCode);
        var relogin = await factory.LoginAsync();
        Assert.Equal("部署所有者", relogin.User.DisplayName);
    }

    [Fact]
    public async Task NewAccounts_JoinOnlyTheClassTheyAreCreatedIn()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        using var client = factory.CreateClient();
        (await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin.AccessToken, new CreateUserRequest
        {
            Username = "no.class", DisplayName = "未分班", Password = "No-Class-Password-2026",
        }))).EnsureSuccessStatusCode();
        (await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin.AccessToken, new CreateUserRequest
        {
            Username = "with.class", DisplayName = "已分班", Password = "With-Class-Password-2026", ClassId = DefaultClassId,
        }))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Bearer(HttpMethod.Post, "/api/users", admin.AccessToken, new CreateUserRequest
        {
            Username = "bad.class", DisplayName = "错班", Password = "Bad-Class-Password-2026", ClassId = Guid.NewGuid(),
        }))).StatusCode);

        var noClass = await factory.LoginAsync("no.class", "No-Class-Password-2026");
        Assert.Empty((await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/me/classes", noClass.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassSummary>>())!);
        var withClass = await factory.LoginAsync("with.class", "With-Class-Password-2026");
        Assert.Equal(DefaultClassId, Assert.Single((await (await client.SendAsync(Bearer(HttpMethod.Get, "/api/me/classes", withClass.AccessToken)))
            .Content.ReadFromJsonAsync<List<ClassSummary>>())!).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upgrade_RemovesUnusedLegacyDefaultClassOnlyWhenOtherClassesExist(bool hasOtherClass)
    {
        // 第一次启动得到完整数据库，然后模拟旧版本留下的“默认班级”、在用设备与全员成员关系，再重启升级。
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "legacy.db");
        var credentialId = Guid.NewGuid();
        var otherClassId = Guid.NewGuid();
        await using (var first = ForDatabase(databasePath))
        {
            await first.LoginAsync();
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Classrooms.Remove(await db.Classrooms.SingleAsync(x => x.Id == DefaultClassId));
            await db.SaveChangesAsync();
            db.Classrooms.Add(new Classroom { Id = Classroom.LegacyDefaultId, Name = "默认班级", CreatedAt = now, UpdatedAt = now });
            if (hasOtherClass) db.Classrooms.Add(new Classroom { Id = otherClassId, Name = "801", CreatedAt = now, UpdatedAt = now });
            db.PluginCredentials.Add(new PluginCredential
            {
                Id = credentialId, Name = "旧设备", TokenHash = new string('a', 64), ClassroomId = Classroom.LegacyDefaultId,
                Assigned = true, Enabled = true, CreatedAt = now, LastSeenAt = now,
            });
            var admin = await db.Users.SingleAsync();
            db.ClassMemberships.Add(new ClassMembership { UserId = admin.Id, ClassroomId = Classroom.LegacyDefaultId, RoleDefinitionId = AccountRole.AdministratorId });
            (await db.SystemMetadata.SingleAsync()).LegacyDefaultClassMigrated = false;
            await db.SaveChangesAsync();
        }

        await using var second = ForDatabase(databasePath);
        await second.LoginAsync();
        using var check = second.Services.CreateScope();
        var upgraded = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = await upgraded.PluginCredentials.SingleAsync(x => x.Id == credentialId);
        if (hasOtherClass)
        {
            Assert.False(await upgraded.Classrooms.AnyAsync(x => x.Id == Classroom.LegacyDefaultId));
            Assert.False(await upgraded.ClassMemberships.AnyAsync(x => x.ClassroomId == Classroom.LegacyDefaultId));
            Assert.False(credential.Assigned);
            Assert.Null(credential.ClassroomId);
            Assert.Equal("原默认班级", credential.ClassNameRemark);
        }
        else
        {
            // 单班级部署：遗留班级就是唯一在用的班级，保留为普通班级。
            Assert.True(await upgraded.Classrooms.AnyAsync(x => x.Id == Classroom.LegacyDefaultId));
            Assert.True(credential.Assigned);
        }
        Assert.True((await upgraded.SystemMetadata.SingleAsync()).LegacyDefaultClassMigrated);
    }

    private static string Csrf(string html)
    {
        var match = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "页面必须包含 CSRF 令牌");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
