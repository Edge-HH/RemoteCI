using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
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
/// M7：聊天机器人执行账号/班级管理高危操作前，必须经网页一次性链接 + 账号密码或一次性验证码授权。
/// 覆盖签发守卫、密码/验证码二次校验、单次执行、参数加密、安全戳失效与执行前重新校验。
/// </summary>
public sealed class OperationApprovalTests
{
    private const string ManagerPassword = "Manager-Password-2026";
    private const string NewUserPassword = "New-User-Password-2026";

    [Fact]
    public async Task CreateUser_PasswordStepUp_CreatesOnce_HidesAndEncryptsPassword()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.CreateUser, Guid.Empty,
            new CreateUserRequest { Username = "approval.new", DisplayName = "审批新建", Password = NewUserPassword, Role = UserRole.User });
        Assert.StartsWith("/Approve/", link.Path);

        var anonymous = Anonymous(factory);
        var page = WebUtility.HtmlDecode(await anonymous.GetStringAsync(link.Path));
        Assert.Contains("审批新建", page);
        Assert.Contains("新建账号", page);
        // 摘要与页面都不出现明文密码。
        Assert.DoesNotContain(NewUserPassword, page);

        // 打开链接只展示，不执行。
        Assert.Null(await FindUserAsync(factory, "approval.new"));

        var done = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.NotNull(await FindUserAsync(factory, "approval.new"));

        // 只能使用一次。
        var again = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("这个链接已经使用过", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));

        // 数据库只存加密参数，不含明文密码。
        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OperationApprovals.AsNoTracking().SingleAsync();
        Assert.NotEmpty(row.ProtectedPayload);
        Assert.DoesNotContain(NewUserPassword, row.ProtectedPayload);
        Assert.DoesNotContain(NewUserPassword, row.Summary);
    }

    [Fact]
    public async Task WrongPassword_KeepsLinkUsable_ThenCorrectPasswordWorks()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.CreateUser, Guid.Empty,
            new CreateUserRequest { Username = "approval.retry", DisplayName = "审批重试", Password = NewUserPassword, Role = UserRole.User });
        var anonymous = Anonymous(factory);

        var wrong = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = "wrong-password" }));
        Assert.Contains("密码错误", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        Assert.Null(await FindUserAsync(factory, "approval.retry"));
        // 失败不消耗链接，仍可重试。
        Assert.DoesNotContain("这个链接已经使用过", WebUtility.HtmlDecode(await anonymous.GetStringAsync(link.Path)));

        var done = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.NotNull(await FindUserAsync(factory, "approval.retry"));
    }

    [Fact]
    public async Task MissingCredential_Rejected()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.DeleteUser, Guid.Empty);
        var anonymous = Anonymous(factory);
        var empty = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Contains("请输入账号密码或一次性验证码", WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task TotpCode_StepUp_CreatesUser()
    {
        await using var factory = new TestWebApplicationFactory();
        var (secret, codes) = await EnableAdminTwoFactorAsync(factory);
        // 用恢复码登录取得令牌，不消耗 TOTP 时间步，便于随后用真实验证码授权。
        var token = await LoginWithRecoveryAsync(factory, codes[0]);
        var link = await IssueAsync(factory, token, OperationApprovalActions.CreateUser, Guid.Empty,
            new CreateUserRequest { Username = "approval.totp", DisplayName = "审批验证码", Password = NewUserPassword, Role = UserRole.User });
        var anonymous = Anonymous(factory);

        var bad = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = "000000" }));
        Assert.Contains("验证码错误", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        Assert.Null(await FindUserAsync(factory, "approval.totp"));

        // 用下一个时间步，避免与绑定时的验证码落在同一窗口被判为重放（与 TwoFactorTests 一致）。
        var code = TwoFactorService.CodeAt(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1);
        var done = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = code }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.NotNull(await FindUserAsync(factory, "approval.totp"));
    }

    [Fact]
    public async Task RecoveryCode_StepUp_DeletesUser()
    {
        await using var factory = new TestWebApplicationFactory();
        var (_, codes) = await EnableAdminTwoFactorAsync(factory);
        var token = await LoginWithRecoveryAsync(factory, codes[0]);
        var victim = await SeedUserAsync(factory, "approval.victim", "审批删除", NewUserPassword);
        var link = await IssueAsync(factory, token, OperationApprovalActions.DeleteUser, victim.Id);
        var anonymous = Anonymous(factory);

        var done = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Code"] = codes[1], ["UseRecoveryCode"] = "true" }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.Null(await FindUserAsync(factory, "approval.victim"));
    }

    [Fact]
    public async Task DeleteClass_PasswordStepUp_DeletesClass()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var classId = await SeedClassAsync(factory, admin.AccessToken, "审批删除班");
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.DeleteClass, classId);
        Assert.Contains("审批删除班", WebUtility.HtmlDecode(await Anonymous(factory).GetStringAsync(link.Path)));

        var done = await Anonymous(factory).PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Classrooms.AnyAsync(x => x.Id == classId));
    }

    [Fact]
    public async Task ResetPassword_PasswordStepUp_SetsNewPassword()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var victim = await SeedUserAsync(factory, "approval.reset", "审批改密", "Old-Password-2026");
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.ResetPassword, victim.Id,
            new ResetPasswordRequest { Password = NewUserPassword });
        // 页面不显示新密码。
        Assert.DoesNotContain(NewUserPassword, WebUtility.HtmlDecode(await Anonymous(factory).GetStringAsync(link.Path)));

        var done = await Anonymous(factory).PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        // 新密码可登录，旧密码失效。
        Assert.NotEmpty((await factory.LoginAsync("approval.reset", NewUserPassword)).AccessToken);
        await Assert.ThrowsAsync<HttpRequestException>(() => factory.LoginAsync("approval.reset", "Old-Password-2026"));
    }

    [Fact]
    public async Task UpdateUser_PasswordStepUp_UpdatesDisplayName()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var victim = await SeedUserAsync(factory, "approval.update", "审批改名", NewUserPassword);
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.UpdateUser, victim.Id,
            new UpdateUserRequest { DisplayName = "改名后", Role = UserRole.User, Enabled = true });
        var done = await Anonymous(factory).PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = TestWebApplicationFactory.AdminPassword }));
        Assert.Contains("已完成", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.Equal("改名后", (await FindUserAsync(factory, "approval.update"))!.DisplayName);
    }

    [Fact]
    public async Task Issuance_RequiresManageUsers()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "approval.teacher", "审批老师", NewUserPassword);
        var teacher = await factory.LoginAsync("approval.teacher", NewUserPassword);
        var response = await SendIssueAsync(factory, teacher.AccessToken, OperationApprovalActions.CreateUser, Guid.Empty,
            new CreateUserRequest { Username = "approval.x", DisplayName = "审批越权", Password = NewUserPassword, Role = UserRole.User });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Issuance_NonAdminCannotTargetAdmin()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "approval.manager", "审批管理员", ManagerPassword, UserRole.User, UserPermissions.ManageUsers);
        var victim = await SeedUserAsync(factory, "approval.adminvictim", "审批管理员目标", NewUserPassword, UserRole.Admin);
        var manager = await factory.LoginAsync("approval.manager", ManagerPassword);
        var response = await SendIssueAsync(factory, manager.AccessToken, OperationApprovalActions.DeleteUser, victim.Id);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SecurityStampChange_InvalidatesLink()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.DeleteUser, Guid.Empty);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByNameAsync(TestWebApplicationFactory.AdminUsername);
            await users.UpdateSecurityStampAsync(user!);
        }
        Assert.Contains("账号状态已变化", WebUtility.HtmlDecode(await Anonymous(factory).GetStringAsync(link.Path)));
    }

    [Fact]
    public async Task ExpiredLink_Rejected()
    {
        await using var factory = new TestWebApplicationFactory();
        var admin = await factory.LoginAsync();
        var link = await IssueAsync(factory, admin.AccessToken, OperationApprovalActions.DeleteUser, Guid.Empty);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hash = CalendarFeedService.Hash(link.Path["/Approve/".Length..]);
            await db.OperationApprovals.Where(x => x.TokenHash == hash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAtUnixMs, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));
        }
        Assert.Contains("链接已过期", WebUtility.HtmlDecode(await Anonymous(factory).GetStringAsync(link.Path)));
    }

    [Fact]
    public async Task Execution_ReGuards_WhenTargetBecomesAdmin()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "approval.reguard", "审批重新校验", ManagerPassword, UserRole.User, UserPermissions.ManageUsers);
        var victim = await SeedUserAsync(factory, "approval.reguardtarget", "审批目标升级", NewUserPassword);
        var manager = await factory.LoginAsync("approval.reguard", ManagerPassword);
        var link = await IssueAsync(factory, manager.AccessToken, OperationApprovalActions.DeleteUser, victim.Id);

        // 签发后把目标提升为管理员：执行前重新校验应拦截，并恢复链接以便重试。
        using (var scope = factory.Services.CreateScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            await identities.UpdateUserAsync(victim.Id, new UpdateUserRequest { DisplayName = "审批目标升级", Role = UserRole.Admin, Enabled = true });
        }
        var anonymous = Anonymous(factory);
        var blocked = await anonymous.PostAsync(link.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = ManagerPassword }));
        Assert.Contains("只有系统管理员可以管理管理员账号", WebUtility.HtmlDecode(await blocked.Content.ReadAsStringAsync()));
        Assert.NotNull(await FindUserAsync(factory, "approval.reguardtarget"));
        Assert.DoesNotContain("这个链接已经使用过", WebUtility.HtmlDecode(await anonymous.GetStringAsync(link.Path)));
    }

    // ---------- 辅助 ----------

    private static HttpClient Anonymous(TestWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<OperationApprovalResponse> IssueAsync(
        TestWebApplicationFactory factory, string token, string action, Guid targetId, object? payload = null)
    {
        var response = await SendIssueAsync(factory, token, action, targetId, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OperationApprovalResponse>())!;
    }

    private static Task<HttpResponseMessage> SendIssueAsync(
        TestWebApplicationFactory factory, string token, string action, Guid targetId, object? payload = null) =>
        factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/me/operation-approvals", token, new { action, targetId, payload }));

    private static async Task<AppUser?> FindUserAsync(TestWebApplicationFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(username);
    }

    private static async Task<AppUser> SeedUserAsync(
        TestWebApplicationFactory factory, string username, string displayName, string password,
        UserRole role = UserRole.User, UserPermissions granted = UserPermissions.None)
    {
        using var scope = factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var created = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = username, DisplayName = displayName, Password = password, Role = role, GrantedPermissions = granted,
        });
        return (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(created.Id.ToString()))!;
    }

    private static async Task<Guid> SeedClassAsync(TestWebApplicationFactory factory, string adminToken, string name)
    {
        var created = await factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/classes", adminToken, new CreateClassRequest { Name = name }));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClassDetail>())!.Id;
    }

    private static async Task<(string Secret, string[] Codes)> EnableAdminTwoFactorAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByNameAsync(TestWebApplicationFactory.AdminUsername))!;
        var twoFactor = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var token = twoFactor.BeginSetup(user);
        var setup = twoFactor.ReadSetup(user, token);
        var codes = await twoFactor.EnableAsync(user, token, TwoFactorService.CodeAt(setup.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), default);
        return (setup.Secret, codes);
    }

    private static async Task<string> LoginWithRecoveryAsync(TestWebApplicationFactory factory, string recoveryCode)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = TestWebApplicationFactory.AdminUsername,
            Password = TestWebApplicationFactory.AdminPassword,
            TwoFactorCode = recoveryCode,
            UseRecoveryCode = true,
            DeviceName = "Approval test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }
}