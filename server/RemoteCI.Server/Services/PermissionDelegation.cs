using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>
/// 授权边界：非管理员通过“人员管理”或“角色配置”修改他人或角色的权限时，只能授予自己拥有的权限；
/// 收回任何权限都允许（只会减少权限）。超出自身权限、且目标原本没有的位保持原状，防止借此给自己或他人提权。
/// 系统管理员不受限制。
/// </summary>
public readonly record struct PermissionDelegation(
    UserRole ActorRole, UserPermissions ActorPermissions, ManagementPermissions ActorManagement)
{
    /// <summary>不做限制（系统管理员，或服务端内部调用）。</summary>
    public static PermissionDelegation Unrestricted => new(UserRole.Admin, UserPermissions.All, ManagementPermissions.All);

    private bool IsAdmin => ActorRole == UserRole.Admin;

    /// <summary>角色默认权限：新增的位必须是操作者拥有的。</summary>
    public UserPermissions ClampDefaults(UserPermissions requested, UserPermissions existing) =>
        IsAdmin ? requested : requested & (ActorPermissions | existing);

    /// <inheritdoc cref="ClampDefaults(UserPermissions, UserPermissions)"/>
    public ManagementPermissions ClampDefaults(ManagementPermissions requested, ManagementPermissions existing) =>
        IsAdmin ? requested : requested & (ActorManagement | existing);

    /// <summary>
    /// 个人调整：新增的位必须是操作者拥有的；操作者没有的位也不能撤销原有的“收回”（撤销收回等于授予）。
    /// </summary>
    public (UserPermissions Granted, UserPermissions Revoked) ClampAdjustment(
        UserPermissions granted, UserPermissions revoked, UserPermissions existingGranted, UserPermissions existingRevoked) =>
        IsAdmin
            ? (granted, revoked)
            : (granted & (ActorPermissions | existingGranted), revoked | (existingRevoked & ~ActorPermissions));

    /// <inheritdoc cref="ClampAdjustment(UserPermissions, UserPermissions, UserPermissions, UserPermissions)"/>
    public (ManagementPermissions Granted, ManagementPermissions Revoked) ClampAdjustment(
        ManagementPermissions granted, ManagementPermissions revoked,
        ManagementPermissions existingGranted, ManagementPermissions existingRevoked) =>
        IsAdmin
            ? (granted, revoked)
            : (granted & (ActorManagement | existingGranted), revoked | (existingRevoked & ~ActorManagement));
}
