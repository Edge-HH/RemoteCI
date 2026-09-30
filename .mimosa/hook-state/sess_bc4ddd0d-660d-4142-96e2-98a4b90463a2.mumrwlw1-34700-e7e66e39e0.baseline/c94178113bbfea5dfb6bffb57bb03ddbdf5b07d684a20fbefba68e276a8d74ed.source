using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 持久化扩展的全局开关和账号自己的手表展示偏好。
/// 扩展调用只依赖独立的 RunExtensions 权限和这里的逐扩展策略。
/// </summary>
public sealed class ExtensionPolicyService(AppDbContext db)
{
    public async Task<bool> EnsureRegisteredAsync(
        IEnumerable<ExtensionDefinition> extensions,
        CancellationToken ct = default)
    {
        var ids = extensions
            .Select(x => ExtensionId.Parse(x.Id, nameof(extensions)).Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return false;

        var existing = await db.ExtensionPolicies.Where(x => ids.Contains(x.ExtensionId))
            .Select(x => x.ExtensionId).ToListAsync(ct);
        var missing = ids.Except(existing, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return false;

        var now = DateTimeOffset.UtcNow;
        db.ExtensionPolicies.AddRange(missing.Select(id => new ExtensionPolicy
        {
            ExtensionId = id,
            Enabled = true,
            // 新扩展默认只供管理员使用，必须由管理员明确开放给普通账号。
            AllowNonAdmin = false,
            UpdatedAt = now,
        }));
        await TouchAccountVersionAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ExtensionControlItem>> ListForUserAsync(
        Guid userId,
        UserRole role,
        UserPermissions permissions,
        IReadOnlyList<ExtensionDefinition> definitions,
        CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(definitions, ct);
        var ids = definitions.Select(x => x.Id).ToArray();
        var policies = await db.ExtensionPolicies.AsNoTracking()
            .Where(x => ids.Contains(x.ExtensionId))
            .ToDictionaryAsync(x => x.ExtensionId, StringComparer.Ordinal, ct);
        var preferences = await db.UserExtensionPreferences.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.ExtensionId))
            .ToDictionaryAsync(x => x.ExtensionId, x => x.ShowOnWatch, StringComparer.Ordinal, ct);

        return definitions.Where(definition => policies.ContainsKey(definition.Id)).Select(definition =>
        {
            var policy = policies[definition.Id];
            var allowedByRole = role == UserRole.Admin ||
                (permissions.HasFlag(UserPermissions.RunExtensions) && policy.AllowNonAdmin);
            var canInvoke = policy.Enabled && allowedByRole &&
                permissions.HasFlag(UserPermissions.RunExtensions);
            return new ExtensionControlItem(
                definition,
                policy.Enabled,
                policy.AllowNonAdmin,
                preferences.GetValueOrDefault(definition.Id, true),
                canInvoke);
        }).Where(item => role == UserRole.Admin || item.CanInvoke).ToArray();
    }

    public async Task UpdateAdminAsync(
        Guid userId,
        string extensionId,
        bool enabled,
        bool allowNonAdmin,
        bool showOnWatch,
        CancellationToken ct = default)
    {
        var policy = await db.ExtensionPolicies.SingleOrDefaultAsync(x => x.ExtensionId == extensionId, ct)
            ?? throw new InvalidOperationException("扩展功能不存在或尚未同步");
        policy.Enabled = enabled;
        policy.AllowNonAdmin = allowNonAdmin;
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await SetPreferenceCoreAsync(userId, extensionId, showOnWatch, ct);
        await TouchAccountVersionAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdatePersonalAsync(
        Guid userId,
        string extensionId,
        bool showOnWatch,
        CancellationToken ct = default)
    {
        var policy = await db.ExtensionPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExtensionId == extensionId, ct)
            ?? throw new InvalidOperationException("扩展功能不存在或尚未同步");
        if (!policy.Enabled || !policy.AllowNonAdmin)
            throw new InvalidOperationException("管理员尚未向普通账号开放此扩展");
        await SetPreferenceCoreAsync(userId, extensionId, showOnWatch, ct);
        await TouchAccountVersionAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplyAccessAsync(UserProfile profile, CancellationToken ct = default)
    {
        var policies = await db.ExtensionPolicies.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
        var hidden = await db.UserExtensionPreferences.AsNoTracking()
            .Where(x => x.UserId == profile.Id && !x.ShowOnWatch)
            .Select(x => x.ExtensionId).ToListAsync(ct);
        ApplyAccess(profile, policies, hidden);
    }

    public static void ApplyAccess(
        UserProfile profile,
        IReadOnlyCollection<ExtensionPolicy> policies,
        IReadOnlyCollection<string> hiddenExtensionIds)
    {
        var allowed = profile.Permissions.HasFlag(UserPermissions.RunExtensions)
            ? policies.Where(policy => profile.Role == UserRole.Admin || policy.AllowNonAdmin)
                .Select(policy => policy.ExtensionId).Order(StringComparer.Ordinal).ToList()
            : [];
        var hidden = hiddenExtensionIds.ToHashSet(StringComparer.Ordinal);
        profile.AllowedExtensionIds = allowed;
        profile.VisibleExtensionIds = allowed.Where(id => !hidden.Contains(id)).ToList();
    }

    private async Task SetPreferenceCoreAsync(
        Guid userId,
        string extensionId,
        bool showOnWatch,
        CancellationToken ct)
    {
        var preference = await db.UserExtensionPreferences.SingleOrDefaultAsync(
            x => x.UserId == userId && x.ExtensionId == extensionId, ct);
        if (preference is null)
        {
            db.UserExtensionPreferences.Add(new UserExtensionPreference
            {
                UserId = userId,
                ExtensionId = extensionId,
                ShowOnWatch = showOnWatch,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            preference.ShowOnWatch = showOnWatch;
            preference.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task TouchAccountVersionAsync(CancellationToken ct) =>
        await db.SystemMetadata.Where(x => x.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AccountVersion, x => x.AccountVersion + 1), ct);
}

public sealed record ExtensionControlItem(
    ExtensionDefinition Definition,
    bool Enabled,
    bool AllowNonAdmin,
    bool ShowOnWatch,
    bool CanInvoke);
