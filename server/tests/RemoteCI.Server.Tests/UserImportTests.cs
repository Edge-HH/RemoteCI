using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class UserImportTests
{
    [Fact]
    public async Task Import_NonAdminDelegation_RejectsAdministratorRole()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserImportService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        await classrooms.CreateAsync("文本导入权限班");

        var delegation = new PermissionDelegation(UserRole.User, UserPermissions.None, ManagementPermissions.ImportUsers);
        var result = await service.ImportAsync("guard.admin,试图创建管理员,文本导入权限班,管理员", null, null, delegation);

        Assert.Equal(0, result.Created);
        Assert.Contains(result.Failures, x => x.Contains("仅系统管理员"));
    }

    [Fact]
    public async Task Import_AdminDelegation_CreatesAdministratorWithoutClassMembership()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UserImportService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        await classrooms.CreateAsync("文本导入管理班");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.ImportAsync(
            "guard.owner,合法管理员,文本导入管理班,管理员,Guard-Owner-Password-2026", null, null, PermissionDelegation.Unrestricted);

        Assert.Equal(1, result.Created);
        var created = await db.Users.SingleAsync(x => x.UserName == "guard.owner");
        Assert.Equal(UserRole.Admin, created.Role);
        Assert.False(await db.ClassMemberships.AnyAsync(x => x.UserId == created.Id));
    }
}