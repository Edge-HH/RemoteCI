using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class RoleAndBackupTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public RoleAndBackupTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task VoiceRolePermissionPersistsAndCanBeRevokedWithoutGrantingNotifications()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<AccountRoleService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var role = await roles.CreateAsync("Voice Sender", UserPermissions.SendVoiceMessages);
        var user = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "role.voice", DisplayName = "Voice Sender", Password = "Voice-Role-2026", RoleId = role.Id,
        });
        Assert.True(user.EffectivePermissions.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.False(user.EffectivePermissions.HasFlag(UserPermissions.SendNotifications));
        await roles.UpdateAsync(role.Id, role.Name, UserPermissions.None);
        var refreshed = await identities.GetProfileAsync(user.Id);
        Assert.False(refreshed!.Permissions.HasFlag(UserPermissions.SendVoiceMessages));
    }

    [Fact]
    public async Task CustomRoleDefaultsCombineWithPersonalGrants()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<AccountRoleService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var role = await roles.CreateAsync("Class Leader", UserPermissions.AccessWebUi | UserPermissions.ManageSchedule);
        var user = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "role.combine", DisplayName = "Role Combine", Password = "Role-Combine-2026",
            RoleId = role.Id, GrantedPermissions = UserPermissions.TeacherComing,
        });
        Assert.Equal(role.Id, user.RoleId);
        Assert.True(user.EffectivePermissions.HasFlag(UserPermissions.AccessWebUi));
        Assert.True(user.EffectivePermissions.HasFlag(UserPermissions.ManageSchedule));
        Assert.True(user.EffectivePermissions.HasFlag(UserPermissions.TeacherComing));
        Assert.False(user.EffectivePermissions.HasFlag(UserPermissions.SystemControl));
    }

    [Fact]
    public async Task AdministratorRoleIsImmutableAndAssignedRoleCannotBeDeleted()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<AccountRoleService>();
        await Assert.ThrowsAsync<IdentityOperationException>(() => roles.UpdateAsync(AccountRole.AdministratorId, "Changed", UserPermissions.None));
        var role = await roles.CreateAsync("In Use", UserPermissions.AccessWebUi);
        await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().CreateUserAsync(new CreateUserRequest
        { Username="role.inuse", DisplayName="In Use", Password="Role-In-Use-2026", RoleId=role.Id });
        await Assert.ThrowsAsync<IdentityOperationException>(() => roles.DeleteAsync(role.Id));
    }

    [Fact]
    public async Task BackupDefaultsAndEncryptedExportAreValid()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BackupConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1);
        Assert.True(settings.Enabled);
        Assert.Equal(BackupCadence.Daily, settings.Cadence);
        Assert.Equal(TimeSpan.FromHours(2), settings.TimeOfDay);
        Assert.Equal(7, settings.MaxBackups);
        var archive = scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>();
        var encrypted = await archive.ExportEncryptedAsync("backup-password");
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(TestWebApplicationFactory.AdminPassword), encrypted);
        var snapshot = archive.ReadEncrypted(encrypted, "backup-password");
        Assert.Contains(snapshot.Users, x => x.Username == TestWebApplicationFactory.AdminUsername);
        Assert.Throws<InvalidDataException>(() => archive.ReadEncrypted(encrypted, "wrong-password"));
    }

    [Fact]
    public async Task VersionOneBackupRestoresLegacySystemControlAsBothSplitPermissions()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        Guid userId;
        ConfigurationSnapshot snapshot;
        using (var scope = factory.Services.CreateScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            var user = await identities.CreateUserAsync(new CreateUserRequest
            {
                Username = "backup.v1.control",
                DisplayName = "旧备份控制账号",
                Password = "Backup-V1-Control-2026",
                GrantedPermissions = UserPermissions.SystemControl,
            });
            userId = user.Id;
            snapshot = (await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CaptureAsync())
                with { Version = 1 };
            await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().ApplyAsync(snapshot);
        }

        using var verificationScope = factory.Services.CreateScope();
        var restored = (await verificationScope.ServiceProvider.GetRequiredService<IdentityCoordinator>()
            .ListUsersAsync()).Single(x => x.Id == userId);
        Assert.True(restored.GrantedPermissions.HasFlag(UserPermissions.PowerControl));
        Assert.True(restored.GrantedPermissions.HasFlag(UserPermissions.MainMenuControl));
    }
}
