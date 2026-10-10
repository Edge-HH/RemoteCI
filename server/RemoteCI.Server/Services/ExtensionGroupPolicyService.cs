using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 扩展插件（扩展分组）的班级自治开关：系统管理员逐个插件决定是否允许班主任自行管理本班设置。
/// 开关对全部班级生效；没有记录的插件视为不开放。
/// </summary>
public sealed class ExtensionGroupPolicyService(AppDbContext db)
{
    /// <summary>允许班主任自行管理的插件分组 Id。</summary>
    public async Task<IReadOnlySet<string>> ListClassAdminAllowedAsync(CancellationToken ct = default) =>
        (await db.ExtensionGroupPolicies.AsNoTracking()
            .Where(x => x.AllowClassAdmin)
            .Select(x => x.GroupId)
            .ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);

    public Task<bool> IsClassAdminAllowedAsync(string groupId, CancellationToken ct = default) =>
        db.ExtensionGroupPolicies.AsNoTracking().AnyAsync(x => x.GroupId == groupId && x.AllowClassAdmin, ct);

    /// <summary>设置某插件是否允许班主任自行管理；插件可以尚未上报，开关会在插件上线后生效。</summary>
    public async Task SetClassAdminAllowedAsync(string groupId, bool allowed, CancellationToken ct = default)
    {
        if (!IsValidGroupId(groupId)) throw new ArgumentException("扩展插件 Id 无效", nameof(groupId));
        var policy = await db.ExtensionGroupPolicies.SingleOrDefaultAsync(x => x.GroupId == groupId, ct);
        if (policy is null)
        {
            if (!allowed) return;
            db.ExtensionGroupPolicies.Add(policy = new ExtensionGroupPolicy { GroupId = groupId });
        }
        policy.AllowClassAdmin = allowed;
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>与 <see cref="ExtensionId"/> 相同的格式约束：非空、无首尾空白、不超过最大长度。</summary>
    public static bool IsValidGroupId(string? groupId) =>
        !string.IsNullOrWhiteSpace(groupId) &&
        string.Equals(groupId, groupId.Trim(), StringComparison.Ordinal) &&
        groupId.Length <= ExtensionId.MaxLength;
}
