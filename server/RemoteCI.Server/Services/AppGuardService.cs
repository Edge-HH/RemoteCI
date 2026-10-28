using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 应用守护策略：系统管理员设置全局默认，单台设备可以整体覆盖。生效策略下发给声明了
/// <see cref="RemoteCiCapabilities.AppGuard"/> 的插件，由插件部署设备外的守护进程执行退出自启、意外退出重启与防删除恢复。
/// 每台设备的生效策略可能不同，所以按连接逐个发送，不使用同一份广播。
/// </summary>
public sealed class AppGuardService(
    AppDbContext db,
    PeerRegistry peers,
    UserNotificationService notifications,
    TimeProvider time,
    ILogger<AppGuardService> logger)
{
    public const int EventRetentionDays = 90;
    public const int MaxEventsPerBatch = 50;

    public async Task<AppGuardPolicy> GetGlobalAsync(CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return FromGlobal(row);
    }

    public async Task<AppGuardPolicy> SetGlobalAsync(AppGuardPolicy policy, CancellationToken ct = default)
    {
        var normalized = policy.Normalized();
        var row = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        row.AppGuardEnabled = normalized.Enabled;
        row.AppGuardRestartAfterExit = normalized.RestartAfterExit;
        row.AppGuardRestartDelayMinutes = normalized.RestartAfterExitDelayMinutes;
        row.AppGuardRestartOnCrash = normalized.RestartOnCrash;
        row.AppGuardPreventDeletion = normalized.PreventDeletion;
        row.AppGuardBackupIntervalHours = normalized.BackupIntervalHours;
        row.AppGuardEnhancedMode = normalized.EnhancedMode;
        row.AppGuardRandomBackupPath = normalized.RandomBackupPath;
        row.AppGuardBackupDirectoryName = normalized.BackupDirectoryName;
        row.AppGuardProcessName = normalized.ProcessName;
        row.AppGuardBackupPathsJson = SerializePaths(normalized);
        await db.SaveChangesAsync(ct);
        await PushToOnlineAsync(null, ct);
        return FromGlobal(row);
    }

    /// <summary>组织名变化后重新下发，使恢复提示里的管理方名称立即更新。</summary>
    public Task OrganizationNameChangedAsync(CancellationToken ct = default) => PushToOnlineAsync(null, ct);

    public async Task<AppGuardDevicePolicyView> GetDeviceAsync(Guid credentialId, CancellationToken ct = default)
    {
        await RequireCredentialAsync(credentialId, ct);
        var row = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var entry = await db.AppGuardOverrides.AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == credentialId, ct);
        return new AppGuardDevicePolicyView
        {
            CredentialId = credentialId,
            Overridden = entry is not null,
            Policy = entry is null ? FromGlobal(row) : FromOverride(entry, row.OrganizationName),
        };
    }

    public async Task<AppGuardDevicePolicyView> SetOverrideAsync(Guid credentialId, AppGuardPolicy policy, CancellationToken ct = default)
    {
        await RequireCredentialAsync(credentialId, ct);
        var normalized = policy.Normalized();
        var entry = await db.AppGuardOverrides.SingleOrDefaultAsync(x => x.CredentialId == credentialId, ct);
        if (entry is null)
        {
            entry = new AppGuardOverride { CredentialId = credentialId };
            db.AppGuardOverrides.Add(entry);
        }
        entry.Enabled = normalized.Enabled;
        entry.RestartAfterExit = normalized.RestartAfterExit;
        entry.RestartDelayMinutes = normalized.RestartAfterExitDelayMinutes;
        entry.RestartOnCrash = normalized.RestartOnCrash;
        entry.PreventDeletion = normalized.PreventDeletion;
        entry.BackupIntervalHours = normalized.BackupIntervalHours;
        entry.EnhancedMode = normalized.EnhancedMode;
        entry.RandomBackupPath = normalized.RandomBackupPath;
        entry.BackupDirectoryName = normalized.BackupDirectoryName;
        entry.ProcessName = normalized.ProcessName;
        entry.BackupPathsJson = SerializePaths(normalized);
        entry.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await PushToOnlineAsync(credentialId, ct);
        return await GetDeviceAsync(credentialId, ct);
    }

    public async Task<AppGuardDevicePolicyView> ClearOverrideAsync(Guid credentialId, CancellationToken ct = default)
    {
        await RequireCredentialAsync(credentialId, ct);
        await db.AppGuardOverrides.Where(x => x.CredentialId == credentialId).ExecuteDeleteAsync(ct);
        await PushToOnlineAsync(credentialId, ct);
        return await GetDeviceAsync(credentialId, ct);
    }

    /// <summary>已设置覆盖的设备，供管理页标注“自定义”。</summary>
    public async Task<IReadOnlyDictionary<Guid, AppGuardPolicy>> GetOverridesAsync(CancellationToken ct = default)
    {
        var organization = await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => x.OrganizationName).SingleAsync(ct);
        var rows = await db.AppGuardOverrides.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(x => x.CredentialId, x => FromOverride(x, organization));
    }

    public async Task<AppGuardPolicy> GetEffectiveAsync(Guid? credentialId, CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        if (credentialId is { } id &&
            await db.AppGuardOverrides.AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == id, ct) is { } entry)
            return FromOverride(entry, row.OrganizationName);
        return FromGlobal(row);
    }

    /// <summary>插件上报能力后补发生效策略；策略未启用时也要发送，插件据此注销已部署的守护进程。</summary>
    public async Task SendToConnectionAsync(Guid connectionId, Guid? credentialId, CancellationToken ct = default)
    {
        var policy = await GetEffectiveAsync(credentialId, ct);
        await peers.SendToPluginConnectionAsync(connectionId, Envelope.AppGuardPolicy(policy), ct);
    }

    /// <summary>
    /// 记录插件上报的守护事件；“已自动恢复”和“恢复失败”另外通知系统管理员。
    /// 事件时间来自教室电脑，只做合理性截断，排序以服务端接收时间兜底。
    /// </summary>
    public async Task RecordEventsAsync(Guid credentialId, Guid? classId, AppGuardEventBatch batch, CancellationToken ct = default)
    {
        var credential = await db.PluginCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credentialId, ct);
        if (credential is null || batch.Events.Count == 0) return;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var deviceName = DeviceAlertEvaluator.DeviceName(credential);
        var rows = batch.Events
            .Where(x => !string.IsNullOrWhiteSpace(x.Kind))
            .Take(MaxEventsPerBatch)
            .Select(x => new AppGuardEventRecord
            {
                CredentialId = credentialId,
                ClassId = classId,
                DeviceName = Truncate(deviceName, 200),
                Kind = Truncate(x.Kind.Trim(), 40),
                Detail = string.IsNullOrWhiteSpace(x.Detail) ? null : Truncate(x.Detail.Trim(), 1000),
                AtUnixMs = x.AtUnixMs > 0 && x.AtUnixMs <= now ? x.AtUnixMs : now,
                ReceivedAtUnixMs = now,
            })
            .ToList();
        if (rows.Count == 0) return;
        db.AppGuardEvents.AddRange(rows);
        var cutoff = now - (long)TimeSpan.FromDays(EventRetentionDays).TotalMilliseconds;
        await db.SaveChangesAsync(ct);
        await db.AppGuardEvents.Where(x => x.ReceivedAtUnixMs < cutoff).ExecuteDeleteAsync(ct);

        var alerts = rows.Where(x => x.Kind is AppGuardEventKinds.Restored or AppGuardEventKinds.RestoreFailed).ToList();
        if (alerts.Count == 0) return;
        var admins = await db.Users.AsNoTracking()
            .Where(x => x.Role == UserRole.Admin && x.Enabled).Select(x => x.Id).ToListAsync(ct);
        foreach (var alert in alerts)
        {
            var restored = alert.Kind == AppGuardEventKinds.Restored;
            var title = restored ? $"{deviceName}：ClassIsland 已自动恢复" : $"{deviceName}：ClassIsland 恢复失败";
            var body = restored
                ? "检测到 ClassIsland 被删除，应用守护已从备份恢复并重新启动。"
                : $"检测到 ClassIsland 被删除，但自动恢复失败：{alert.Detail ?? "未知原因"}";
            try
            {
                await notifications.PublishAsync(admins, UserNotificationKinds.AppGuard, title, body, null, ct,
                    classId: classId, link: "/AppGuard");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to notify admins about app guard event {Kind}", alert.Kind);
            }
        }
    }

    public async Task<IReadOnlyList<AppGuardEventView>> GetRecentEventsAsync(
        Guid? credentialId = null, int limit = 100, CancellationToken ct = default)
    {
        var query = db.AppGuardEvents.AsNoTracking();
        if (credentialId is { } id) query = query.Where(x => x.CredentialId == id);
        var rows = await query.OrderByDescending(x => x.AtUnixMs).ThenByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return rows.Select(x => new AppGuardEventView
        {
            CredentialId = x.CredentialId,
            DeviceName = x.DeviceName,
            Kind = x.Kind,
            KindName = AppGuardEventKinds.ChineseName(x.Kind),
            Detail = x.Detail,
            AtUnixMs = x.AtUnixMs,
        }).ToList();
    }

    /// <summary>向在线且声明守护能力的插件与守护连接下发各自的生效策略。</summary>
    private async Task PushToOnlineAsync(Guid? onlyCredential, CancellationToken ct)
    {
        var targets = peers.GetAppGuardConnections()
            .Where(x => onlyCredential is null || x.CredentialId == onlyCredential)
            .ToList();
        if (targets.Count == 0) return;
        var row = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var overrides = await db.AppGuardOverrides.AsNoTracking().ToDictionaryAsync(x => x.CredentialId, ct);
        foreach (var target in targets)
        {
            var policy = overrides.TryGetValue(target.CredentialId, out var entry)
                ? FromOverride(entry, row.OrganizationName)
                : FromGlobal(row);
            await peers.SendToPluginConnectionAsync(target.ConnectionId, Envelope.AppGuardPolicy(policy), ct);
        }
    }

    private async Task RequireCredentialAsync(Guid credentialId, CancellationToken ct)
    {
        if (!await db.PluginCredentials.AsNoTracking().AnyAsync(x => x.Id == credentialId, ct))
            throw new KeyNotFoundException("设备不存在");
    }

    internal static AppGuardPolicy FromGlobal(SystemMetadata row) => new AppGuardPolicy
    {
        Enabled = row.AppGuardEnabled,
        RestartAfterExit = row.AppGuardRestartAfterExit,
        RestartAfterExitDelayMinutes = row.AppGuardRestartDelayMinutes,
        RestartOnCrash = row.AppGuardRestartOnCrash,
        PreventDeletion = row.AppGuardPreventDeletion,
        BackupIntervalHours = row.AppGuardBackupIntervalHours,
        EnhancedMode = row.AppGuardEnhancedMode,
        RandomBackupPath = row.AppGuardRandomBackupPath,
        BackupDirectoryName = row.AppGuardBackupDirectoryName,
        ProcessName = row.AppGuardProcessName,
        BackupPaths = DeserializePaths(row.AppGuardBackupPathsJson),
        OrganizationName = row.OrganizationName ?? AppGuardPolicy.DefaultOrganizationName,
    }.Normalized();

    internal static AppGuardPolicy FromOverride(AppGuardOverride entry, string? organizationName) => new AppGuardPolicy
    {
        Enabled = entry.Enabled,
        RestartAfterExit = entry.RestartAfterExit,
        RestartAfterExitDelayMinutes = entry.RestartDelayMinutes,
        RestartOnCrash = entry.RestartOnCrash,
        PreventDeletion = entry.PreventDeletion,
        BackupIntervalHours = entry.BackupIntervalHours,
        EnhancedMode = entry.EnhancedMode,
        RandomBackupPath = entry.RandomBackupPath,
        BackupDirectoryName = entry.BackupDirectoryName,
        ProcessName = entry.ProcessName,
        BackupPaths = DeserializePaths(entry.BackupPathsJson),
        OrganizationName = organizationName ?? AppGuardPolicy.DefaultOrganizationName,
    }.Normalized();

    private static string SerializePaths(AppGuardPolicy policy) => JsonSerializer.Serialize(policy.BackupPaths);

    internal static List<string> DeserializeBackupPaths(string? json) => DeserializePaths(json);

    private static List<string> DeserializePaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
