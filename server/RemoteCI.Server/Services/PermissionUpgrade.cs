using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>
/// 权限细化前的权限位升级：原先合并在其他权限里（或写死给班主任、系统管理员）的功能拆成独立权限后，
/// 已拥有原权限的角色与账号补齐新权限，升级后行为不变。
/// 与数据库迁移 RefinePermissions 中的 SQL 一一对应；导入旧版配置备份时用它补齐。
/// </summary>
public static class PermissionUpgrade
{
    /// <summary>原“人员管理”顺带授予的教室端维护权限。</summary>
    public const UserPermissions SplitFromManageUsers =
        UserPermissions.ManageClassProfile | UserPermissions.RemoteTerminal | UserPermissions.ControlClassIsland |
        UserPermissions.ManagePlugins | UserPermissions.UpgradeSoftware | UserPermissions.ManageDeviceGuard;

    /// <summary>原“人员管理”顺带可用的系统设置页：登录与访问、插件与课表同步。</summary>
    public const ManagementPermissions ManagementFromManageUsers =
        ManagementPermissions.ManageLoginPage | ManagementPermissions.ManagePluginAccess;

    /// <summary>原先只按“本班班主任”身份开放的功能。</summary>
    public const UserPermissions ClassHeadFeatures =
        UserPermissions.ManageClassBoards | UserPermissions.ViewAuditLog | UserPermissions.ViewLessonStats;

    /// <summary>任意角色默认权限或个人新增权限：按原有权限补齐拆出的新权限。</summary>
    public static UserPermissions Split(UserPermissions permissions)
    {
        if (permissions.HasFlag(UserPermissions.SendNotifications)) permissions |= UserPermissions.ClearNotifications;
        if (permissions.HasFlag(UserPermissions.PowerControl)) permissions |= UserPermissions.VolumeControl;
        if (permissions.HasFlag(UserPermissions.MainMenuControl)) permissions |= UserPermissions.MainWindowLayout;
        if (permissions.HasFlag(UserPermissions.ManageUsers)) permissions |= SplitFromManageUsers;
        return permissions;
    }

    /// <summary>
    /// 内置“班主任”角色：在 <see cref="Split"/> 的基础上补齐原先按班主任身份开放的功能，
    /// 并把原“班级自治策略”的开关换算成该角色的“班级信息”与“刷新课表”权限。
    /// </summary>
    public static UserPermissions ClassAdministrator(UserPermissions permissions, ClassSelfServicePolicy policy)
    {
        permissions = Split(permissions) | ClassHeadFeatures;
        if (permissions.HasFlag(UserPermissions.RunExtensions)) permissions |= UserPermissions.ConfigureExtensions;
        if (permissions.HasFlag(UserPermissions.ManageSchedule)) permissions |= UserPermissions.ManageClassProfile;
        if (policy.CanRename || policy.CanChangeAvatar) permissions |= UserPermissions.ManageClassInfo;
        if (policy.CanPullSchedule) permissions |= UserPermissions.PullSchedule;
        return permissions;
    }

    /// <summary>原先拥有“人员管理”的角色或账号补齐对应的系统管理权限。</summary>
    public static ManagementPermissions Management(UserPermissions legacy) =>
        legacy.HasFlag(UserPermissions.ManageUsers) ? ManagementFromManageUsers : ManagementPermissions.None;
}
