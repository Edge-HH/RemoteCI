using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>
/// 班级自治策略：班主任可以在本班自行完成的班级管理操作。
/// 同一结构也表示“某用户在某班级的实际可用操作”，系统管理员恒为 <see cref="All"/>。
/// 扩展插件设置按插件逐个开放，见 <see cref="ExtensionGroupPolicyService"/>。
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
}

/// <summary>系统管理员统一维护的班级自治策略，持久化在 SystemMetadata 上，对全部班级生效。</summary>
public sealed class ClassSelfServiceSettings(AppDbContext db)
{
    public Task<ClassSelfServicePolicy> GetAsync(CancellationToken ct = default) => ReadAsync(db, ct);

    public async Task SetAsync(ClassSelfServicePolicy policy, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        metadata.ClassAdminCanRename = policy.CanRename;
        metadata.ClassAdminCanChangeAvatar = policy.CanChangeAvatar;
        metadata.ClassAdminCanPullSchedule = policy.CanPullSchedule;
        await db.SaveChangesAsync(ct);
    }

    internal static Task<ClassSelfServicePolicy> ReadAsync(AppDbContext db, CancellationToken ct) =>
        db.SystemMetadata.AsNoTracking()
            .Where(metadata => metadata.Id == 1)
            .Select(metadata => new ClassSelfServicePolicy(
                metadata.ClassAdminCanRename,
                metadata.ClassAdminCanChangeAvatar,
                metadata.ClassAdminCanPullSchedule))
            .SingleAsync(ct);
}
