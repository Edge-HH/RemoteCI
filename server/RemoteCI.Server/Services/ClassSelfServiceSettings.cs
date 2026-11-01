using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>
/// 某用户在某班级可以自行完成的班级管理操作，由班级权限决定：
/// “班级信息”（ManageClassInfo）允许改班名与头像，“刷新课表”（PullSchedule）允许从插件拉取课表。
/// 系统管理员恒为 <see cref="All"/>。扩展插件设置见 <see cref="ExtensionGroupPolicyService"/>。
/// </summary>
public sealed record ClassSelfServicePolicy(
    bool CanRename,
    bool CanChangeAvatar,
    bool CanPullSchedule)
{
    public static ClassSelfServicePolicy All { get; } = new(true, true, true);

    public static ClassSelfServicePolicy None { get; } = new(false, false, false);

    /// <summary>升级前的行为：班主任可改名、改头像与拉取课表。</summary>
    public static ClassSelfServicePolicy Default { get; } = new(true, true, true);

    public static ClassSelfServicePolicy From(UserPermissions permissions) => new(
        permissions.HasFlag(UserPermissions.ManageClassInfo),
        permissions.HasFlag(UserPermissions.ManageClassInfo),
        permissions.HasFlag(UserPermissions.PullSchedule));
}

/// <summary>
/// 旧版“班级自治策略”接口的兼容层：权限细化后，这些开关就是内置“班主任”角色的默认权限，
/// 读写都直接映射到该角色的“班级信息”与“刷新课表”权限，与角色配置页保持一致。
/// </summary>
public sealed class ClassSelfServiceSettings(AppDbContext db)
{
    public Task<ClassSelfServicePolicy> GetAsync(CancellationToken ct = default) => ReadAsync(db, ct);

    public async Task SetAsync(ClassSelfServicePolicy policy, CancellationToken ct = default)
    {
        var role = await db.AccountRoles.SingleAsync(x => x.Id == AccountRole.ClassAdministratorId, ct);
        var permissions = role.DefaultPermissions & ~(UserPermissions.ManageClassInfo | UserPermissions.PullSchedule);
        if (policy.CanRename || policy.CanChangeAvatar) permissions |= UserPermissions.ManageClassInfo;
        if (policy.CanPullSchedule) permissions |= UserPermissions.PullSchedule;
        if (permissions == role.DefaultPermissions) return;
        role.DefaultPermissions = permissions;
        role.UpdatedAt = DateTimeOffset.UtcNow;
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        metadata.AccountVersion++;
        await db.SaveChangesAsync(ct);
    }

    internal static async Task<ClassSelfServicePolicy> ReadAsync(AppDbContext db, CancellationToken ct) =>
        ClassSelfServicePolicy.From(await db.AccountRoles.AsNoTracking()
            .Where(x => x.Id == AccountRole.ClassAdministratorId)
            .Select(x => x.DefaultPermissions)
            .SingleOrDefaultAsync(ct));
}
