using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class AccountRoleService(AppDbContext db)
{
    /// <summary>内置“班主任”角色曾用名；批量导入等旧清单仍按此名引用。</summary>
    internal const string LegacyClassAdministratorName = "班管理员";

    public async Task<IReadOnlyList<AccountRoleInfo>> ListAsync(CancellationToken ct = default) =>
        await db.AccountRoles.AsNoTracking().OrderBy(x => x.Kind).ThenBy(x => x.Name)
            .Select(x => new AccountRoleInfo
            {
                Id = x.Id,
                Name = x.Name,
                Kind = x.Kind.ToString(),
                DefaultPermissions = x.Kind == AccountRoleKind.Administrator ? UserPermissions.All : x.DefaultPermissions,
                UserCount = x.Users.Count,
            }).ToListAsync(ct);

    public async Task<AccountRoleInfo> CreateAsync(string name, UserPermissions permissions, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        if (await db.AccountRoles.AnyAsync(x => x.NormalizedName == normalized, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role name already exists");
        var now = DateTimeOffset.UtcNow;
        var role = new AccountRole
        {
            Id = Guid.NewGuid(), Name = name.Trim(), NormalizedName = normalized,
            Kind = AccountRoleKind.Custom, DefaultPermissions = permissions & RolePermissions.Assignable,
            CreatedAt = now, UpdatedAt = now,
        };
        db.AccountRoles.Add(role);
        await db.SaveChangesAsync(ct);
        return new AccountRoleInfo { Id = role.Id, Name = role.Name, Kind = role.Kind.ToString(), DefaultPermissions = role.DefaultPermissions };
    }

    public async Task UpdateAsync(Guid id, string name, UserPermissions permissions, CancellationToken ct = default)
    {
        var role = await db.AccountRoles.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "Role not found");
        if (role.Kind is AccountRoleKind.Administrator)
            throw new IdentityOperationException(ApiErrorCodes.Forbidden, "Administrator role is immutable");
        var normalized = role.Kind is AccountRoleKind.Student or AccountRoleKind.ClassAdministrator
            ? role.NormalizedName
            : NormalizeName(name);
        if (role.Kind == AccountRoleKind.Custom && await db.AccountRoles.AnyAsync(x => x.Id != id && x.NormalizedName == normalized, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role name already exists");
        if (role.Kind == AccountRoleKind.Custom) { role.Name = name.Trim(); role.NormalizedName = normalized; }
        role.DefaultPermissions = permissions & RolePermissions.Assignable;
        role.UpdatedAt = DateTimeOffset.UtcNow;
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.AccountVersion++;
        var version = metadata.AccountVersion;
        await db.Users.Where(x => x.RoleDefinitionId == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Version, version)
            .SetProperty(x => x.UpdatedAt, role.UpdatedAt), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var role = await db.AccountRoles.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "Role not found");
        if (role.Kind != AccountRoleKind.Custom)
            throw new IdentityOperationException(ApiErrorCodes.Forbidden, "Built-in roles cannot be deleted");
        if (await db.Users.AnyAsync(x => x.RoleDefinitionId == id, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role is still assigned to accounts");
        if (await db.ClassMemberships.AnyAsync(x => x.RoleDefinitionId == id, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role is still assigned to class members");
        db.AccountRoles.Remove(role);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>按显示名或规范化名查找角色（批量导入按角色名引用）。</summary>
    public async Task<AccountRole?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return null;
        // 内置“班主任”曾名“班管理员”，旧导入清单仍可按旧名引用。
        if (trimmed == LegacyClassAdministratorName)
            return await db.AccountRoles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == AccountRole.ClassAdministratorId, ct);
        var normalized = trimmed.ToUpperInvariant();
        return await db.AccountRoles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Name == trimmed || x.NormalizedName == normalized, ct);
    }

    private static string NormalizeName(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 40) throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role name must be 1-40 characters");
        return trimmed.ToUpperInvariant();
    }
}
