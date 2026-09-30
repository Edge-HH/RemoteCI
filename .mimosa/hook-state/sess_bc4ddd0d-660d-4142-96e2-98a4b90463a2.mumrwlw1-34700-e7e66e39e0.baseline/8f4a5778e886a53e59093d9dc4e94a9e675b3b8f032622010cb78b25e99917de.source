using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ExtensionPolicyServiceTests
{
    [Theory]
    [InlineData(" demo")]
    [InlineData("demo ")]
    public async Task ExtensionRegistration_RejectsNonCanonicalIds(string id)
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using var scope = factory.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<ExtensionPolicyService>();

        await Assert.ThrowsAsync<ArgumentException>(() => policies.EnsureRegisteredAsync(
            [new ExtensionDefinition { Id = id, DisplayName = "非法扩展" }]));
    }

    [Fact]
    public async Task ExtensionAccess_RequiresIndependentPermissionAndSupportsPersonalWatchVisibility()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();

        using var scope = factory.Services.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var policies = scope.ServiceProvider.GetRequiredService<ExtensionPolicyService>();
        var definition = new ExtensionDefinition
        {
            Id = "demo.notice",
            DisplayName = "通知扩展",
            RequiredPermission = UserPermissions.SendNotifications,
        };
        Assert.True(await policies.EnsureRegisteredAsync([definition]));

        var withoutExtensionPermission = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "extension.notification.only",
            DisplayName = "只有通知权限",
            Password = "Extension-Policy-Password-2026",
            GrantedPermissions = UserPermissions.SendNotifications,
        });
        var extensionUser = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "extension.allowed",
            DisplayName = "扩展账号",
            Password = "Extension-Allowed-Password-2026",
            GrantedPermissions = UserPermissions.RunExtensions,
        });
        var admin = (await identities.ListUsersAsync()).Single(x => x.Role == UserRole.Admin);

        Assert.Empty(await policies.ListForUserAsync(
            extensionUser.Id, extensionUser.Role, extensionUser.EffectivePermissions, [definition]));

        await policies.UpdateAdminAsync(admin.Id, definition.Id, enabled: true, allowNonAdmin: true, showOnWatch: true);
        Assert.Empty(await policies.ListForUserAsync(
            withoutExtensionPermission.Id,
            withoutExtensionPermission.Role,
            withoutExtensionPermission.EffectivePermissions,
            [definition]));
        Assert.True(Assert.Single(await policies.ListForUserAsync(
            extensionUser.Id, extensionUser.Role, extensionUser.EffectivePermissions, [definition])).CanInvoke);

        await policies.UpdatePersonalAsync(extensionUser.Id, definition.Id, showOnWatch: false);
        var profile = await identities.GetProfileAsync(extensionUser.Id);
        Assert.Contains(definition.Id, profile!.AllowedExtensionIds!);
        Assert.DoesNotContain(definition.Id, profile.VisibleExtensionIds!);

        var mirrored = (await identities.CreateSyncAsync(Classroom.DefaultId)).Accounts.Single(x => x.Id == extensionUser.Id);
        Assert.Contains(definition.Id, mirrored.AllowedExtensionIds!);
        Assert.DoesNotContain(definition.Id, mirrored.VisibleExtensionIds!);
    }
}
