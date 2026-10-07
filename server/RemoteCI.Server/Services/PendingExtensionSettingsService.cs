using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 扩展设置的待补发记录：下发时班级插件离线就持久化，插件重新上线后由 <see cref="ExtensionSettingsReplayService"/> 补发。
/// 同一班级同一分组只保留一条，多次修改按字段合并。
/// </summary>
public sealed class PendingExtensionSettingsService(AppDbContext db)
{
    public async Task RecordAsync(
        Guid classId,
        string groupId,
        IReadOnlyDictionary<string, string?> values,
        Guid requestedByUserId,
        CancellationToken ct = default)
    {
        var row = await db.PendingExtensionSettings.SingleOrDefaultAsync(
            x => x.ClassroomId == classId && x.GroupId == groupId, ct);
        var merged = row is null ? new Dictionary<string, string?>(StringComparer.Ordinal) : Parse(row.ValuesJson);
        foreach (var pair in values) merged[pair.Key] = pair.Value;
        if (row is null)
        {
            row = new PendingExtensionSetting { ClassroomId = classId, GroupId = groupId };
            db.PendingExtensionSettings.Add(row);
        }
        row.ValuesJson = JsonSerializer.Serialize(merged, JsonDefaults.Options);
        row.RequestedByUserId = requestedByUserId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>某个分组在各班级的待补发字段，用于设置页展示。</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>>> ListByGroupAsync(
        string groupId, CancellationToken ct = default) =>
        (await db.PendingExtensionSettings.AsNoTracking().Where(x => x.GroupId == groupId).ToListAsync(ct))
            .ToDictionary(x => x.ClassroomId, x => (IReadOnlyDictionary<string, string?>)Parse(x.ValuesJson));

    public async Task<IReadOnlyList<PendingExtensionSetting>> ListForClassAsync(
        Guid classId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default) =>
        await db.PendingExtensionSettings.AsNoTracking()
            .Where(x => x.ClassroomId == classId && groupIds.Contains(x.GroupId))
            .ToListAsync(ct);

    /// <summary>补发完成后删除；期间若有人再次修改（UpdatedAt 已变化）则保留新记录。</summary>
    public async Task RemoveAsync(PendingExtensionSetting handled, CancellationToken ct = default)
    {
        // SQLite 不能翻译 DateTimeOffset 比较，取回后在内存中比对。
        var row = await db.PendingExtensionSettings.SingleOrDefaultAsync(
            x => x.ClassroomId == handled.ClassroomId && x.GroupId == handled.GroupId, ct);
        if (row is null || row.UpdatedAt != handled.UpdatedAt) return;
        db.PendingExtensionSettings.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public static Dictionary<string, string?> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonDefaults.Options) is { } values
                ? new Dictionary<string, string?>(values, StringComparer.Ordinal)
                : new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// 插件同步扩展分组后，在后台把该班的待补发设置定向发给这条插件连接。
/// 必须在后台执行：命令回执经同一条 WebSocket 返回，在接收循环里等待会自锁。
/// </summary>
public sealed class ExtensionSettingsReplayService(
    IServiceScopeFactory scopes,
    PeerRegistry peers,
    ILogger<ExtensionSettingsReplayService> logger)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    // 插件写入设置后会立即重发分组同步：补发仍在等待回执时不能再次触发，否则同一设置会被写两次。
    private readonly ConcurrentDictionary<(Guid ClassId, string GroupId), byte> _inFlight = new();

    public void Schedule(Guid classId, Guid connectionId, IReadOnlyList<ExtensionGroupDefinition> groups)
    {
        var groupIds = groups.Where(x => x.HasSettings).Select(x => x.Id).ToList();
        if (groupIds.Count == 0) return;
        _ = Task.Run(() => ReplayAsync(classId, connectionId, groupIds));
    }

    /// <summary>测试与调用方可等待的补发入口。</summary>
    public async Task ReplayAsync(Guid classId, Guid connectionId, IReadOnlyCollection<string> groupIds)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var pending = scope.ServiceProvider.GetRequiredService<PendingExtensionSettingsService>();
            foreach (var row in await pending.ListForClassAsync(classId, groupIds))
            {
                if (!_inFlight.TryAdd((classId, row.GroupId), 0)) continue;
                try
                {
                    await ReplayOneAsync(scope.ServiceProvider, pending, row, connectionId);
                }
                finally
                {
                    _inFlight.TryRemove((classId, row.GroupId), out _);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "补发扩展设置失败（班级 {ClassId}）", classId);
        }
    }

    private async Task ReplayOneAsync(
        IServiceProvider services, PendingExtensionSettingsService pending, PendingExtensionSetting row, Guid connectionId)
    {
        var identities = services.GetRequiredService<IdentityCoordinator>();
        var access = services.GetRequiredService<ClassAccessService>();
        var groups = services.GetRequiredService<ExtensionGroupService>();
        var profile = await identities.GetProfileAsync(row.RequestedByUserId);
        // 下发者已被删除、禁用，或此后失去了修改该班扩展设置的权限：放弃补发。
        if (profile is null || !await groups.CanEditSettingsAsync(profile, row.ClassroomId))
        {
            logger.LogInformation("放弃补发扩展设置 {GroupId}（班级 {ClassId}）：下发者已无权限", row.GroupId, row.ClassroomId);
            await pending.RemoveAsync(row);
            return;
        }

        var permissions = await access.GetEffectivePermissionsAsync(
            profile.Id, profile.Role, row.ClassroomId, profile.GrantedPermissions);
        var result = await peers.SendCommandAndWaitToConnectionAsync(
            ExtensionGroupService.CreateCommand(
                profile.WithPermissions(permissions), row.GroupId, PendingExtensionSettingsService.Parse(row.ValuesJson), row.ClassroomId),
            connectionId,
            CommandTimeout);
        // 连接又断开时保留，等下次上线；插件给出明确结果（含校验失败、超时）后都不再重复补发，避免死循环。
        if (result.Code == CommandResultCodes.PluginOffline) return;
        if (!result.Success)
            logger.LogWarning("补发扩展设置 {GroupId} 到班级 {ClassId} 未成功：{Code} {Message}",
                row.GroupId, row.ClassroomId, result.Code, result.Message);
        await pending.RemoveAsync(row);
    }
}
