using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed record DeviceTimelineSegment(DateTimeOffset Start, DateTimeOffset End);

/// <summary>设备状态页与 GET /api/devices/health 的单台设备。</summary>
public sealed record DeviceHealthItem(
    Guid CredentialId,
    Guid ClassId,
    string ClassName,
    IReadOnlyList<string> GroupNames,
    string DeviceName,
    bool Online,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? OfflineSince,
    string PluginVersion,
    string ClassIslandVersion,
    bool Outdated,
    string? CurrentCourse,
    int? VolumePercent,
    bool? Muted,
    int? ProfileDriftCount,
    IReadOnlyList<DeviceTimelineSegment> Timeline);

public sealed record DeviceHealthOverview(
    DateTimeOffset GeneratedAt,
    string ServerVersion,
    DateTimeOffset? TimelineStart,
    DateTimeOffset? TimelineEnd,
    IReadOnlyList<DeviceHealthItem> Devices)
{
    public int OnlineCount => Devices.Count(x => x.Online);
    public int OfflineCount => Devices.Count(x => !x.Online);
    public int OutdatedCount => Devices.Count(x => x.Outdated);
    public int DriftCount => Devices.Count(x => x.ProfileDriftCount > 0);
}

/// <summary>
/// 订阅插件上下线事件并落库，供在线时间条使用。同一凭据有多条连接时只记录整体在线状态的变化，
/// 保留 30 天。写库在后台完成，不阻塞连接注册。
/// </summary>
public sealed class DevicePresenceRecorder(
    PeerRegistry peers,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    ILogger<DevicePresenceRecorder> logger) : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly Channel<PluginPresenceChange> _queue = Channel.CreateBounded<PluginPresenceChange>(
        new BoundedChannelOptions(5_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.OperationsWorkers) peers.PluginPresenceChanged += OnPresenceChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        peers.PluginPresenceChanged -= OnPresenceChanged;
        return base.StopAsync(cancellationToken);
    }

    private void OnPresenceChanged(PluginPresenceChange change) => _queue.Writer.TryWrite(change);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<PluginPresenceChange>();
                while (batch.Count < 200 && _queue.Reader.TryRead(out var change)) batch.Add(change);
                try { await WriteAsync(batch, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "记录插件上下线失败");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }

    private async Task WriteAsync(IReadOnlyList<PluginPresenceChange> batch, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connections = peers.GetPluginDeviceSnapshots()
            .Where(x => x.PluginCredentialId is not null)
            .GroupBy(x => x.PluginCredentialId!.Value)
            .ToDictionary(x => x.Key, x => x.Count());
        foreach (var change in batch)
        {
            // 同一凭据仍有其他连接在线时，单条连接的断开或重复接入不改变设备的在线状态。
            var remaining = connections.GetValueOrDefault(change.CredentialId);
            if (change.Online ? remaining > 1 : remaining > 0) continue;
            db.DevicePresenceEvents.Add(new DevicePresenceEvent
            {
                CredentialId = change.CredentialId,
                ClassId = change.ClassId,
                Online = change.Online,
                AtUnixMs = change.At.ToUnixTimeMilliseconds(),
            });
        }
        await db.SaveChangesAsync(ct);

        var now = timeProvider.GetUtcNow();
        if (now < _nextPrune) return;
        _nextPrune = now.AddHours(12);
        var cutoff = (now - Retention).ToUnixTimeMilliseconds();
        await db.DevicePresenceEvents.Where(x => x.AtUnixMs < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// 离线告警：设备在班级在校时段连续离线超过阈值时，通知系统管理员和该班班主任；
/// 告警过的设备恢复在线后再发一条恢复通知。每次离线只告警一次。
/// </summary>
public sealed class DeviceAlertEvaluator(
    AppDbContext db,
    PeerRegistry peers,
    IStateStore state,
    ScheduleViewService scheduleViews,
    UserNotificationService notifications,
    ILogger<DeviceAlertEvaluator> logger)
{
    /// <summary>在校时段从第一节课前多久开始算。</summary>
    public static readonly TimeSpan SchoolLead = TimeSpan.FromMinutes(30);

    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var settings = await db.SystemMetadata.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SystemMetadata();
        var credentials = await db.PluginCredentials.AsNoTracking()
            .Include(x => x.Classroom)
            .Where(x => x.Assigned && x.Enabled && x.ClassroomId != null)
            .ToListAsync(ct);
        var online = peers.GetPluginDeviceSnapshots()
            .Select(x => x.PluginCredentialId)
            .OfType<Guid>()
            .ToHashSet();
        var states = await db.DeviceAlertStates.ToDictionaryAsync(x => x.CredentialId, ct);
        var known = credentials.Select(x => x.Id).ToHashSet();
        db.DeviceAlertStates.RemoveRange(states.Values.Where(x => !known.Contains(x.CredentialId)));

        var threshold = TimeSpan.FromMinutes(Math.Clamp(
            settings.DeviceAlertThresholdMinutes, DeviceAlertDefaults.MinThresholdMinutes, DeviceAlertDefaults.MaxThresholdMinutes));
        foreach (var credential in credentials)
        {
            var classId = credential.ClassroomId!.Value;
            var className = credential.Classroom?.Name ?? "未知班级";
            states.TryGetValue(credential.Id, out var alert);
            if (online.Contains(credential.Id))
            {
                if (alert is null) continue;
                db.DeviceAlertStates.Remove(alert);
                if (alert.AlertedAtUnixMs is not null && settings.DeviceAlertsEnabled)
                {
                    var duration = now - DateTimeOffset.FromUnixTimeMilliseconds(alert.OfflineSinceUnixMs);
                    await NotifyAsync(settings, classId, UserNotificationKinds.DeviceOnline,
                        $"{className} 教室电脑已恢复在线",
                        $"{DeviceName(credential)} 离线约 {FormatDuration(duration)}，现已重新连接。", ct);
                }
                continue;
            }

            if (alert is null)
            {
                var lastOffline = await db.DevicePresenceEvents.AsNoTracking()
                    .Where(x => x.CredentialId == credential.Id && !x.Online)
                    .OrderByDescending(x => x.AtUnixMs)
                    .Select(x => (long?)x.AtUnixMs)
                    .FirstOrDefaultAsync(ct);
                alert = new DeviceAlertState
                {
                    CredentialId = credential.Id,
                    OfflineSinceUnixMs = lastOffline ?? now.ToUnixTimeMilliseconds(),
                };
                db.DeviceAlertStates.Add(alert);
                states[credential.Id] = alert;
            }
            if (alert.AlertedAtUnixMs is not null || !settings.DeviceAlertsEnabled) continue;

            var offlineSince = DateTimeOffset.FromUnixTimeMilliseconds(alert.OfflineSinceUnixMs);
            var effectiveSince = offlineSince;
            if (settings.DeviceAlertSchoolHoursOnly)
            {
                if (await SchoolHoursAsync(classId, now, ct) is not { } window || now < window.Start || now > window.End)
                    continue;
                // 早上没开机：从在校时段开始计时，而不是从前一天放学关机算起。
                if (window.Start > effectiveSince) effectiveSince = window.Start;
            }
            if (now - effectiveSince < threshold) continue;

            alert.AlertedAtUnixMs = now.ToUnixTimeMilliseconds();
            var offset = ClassClock.Offset(state, classId, now);
            await NotifyAsync(settings, classId, UserNotificationKinds.DeviceOffline,
                $"{className} 教室电脑离线",
                $"{DeviceName(credential)} 已离线 {FormatDuration(now - effectiveSince)}（最后在线 {offlineSince.ToOffset(offset):MM-dd HH:mm}）。" +
                "请检查电脑是否开机、ClassIsland 是否运行以及网络是否正常。", ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<(DateTimeOffset Start, DateTimeOffset End)?> SchoolHoursAsync(
        Guid classId, DateTimeOffset now, CancellationToken ct)
    {
        if (await scheduleViews.GetAsync(classId, ct) is not { } view) return null;
        var offset = ClassClock.Offset(state, classId, now);
        var today = ClassClock.Today(state, classId, now);
        return ClassSchedulePeriods.SchoolHours(ClassSchedulePeriods.FindDay(view.Bundle, today), offset, SchoolLead);
    }

    private async Task NotifyAsync(
        SystemMetadata settings, Guid classId, string kind, string title, string body, CancellationToken ct)
    {
        var recipients = new HashSet<Guid>();
        if (settings.DeviceAlertNotifyAdmins)
            recipients.UnionWith(await db.Users.AsNoTracking()
                .Where(x => x.Role == UserRole.Admin && x.Enabled).Select(x => x.Id).ToListAsync(ct));
        if (settings.DeviceAlertNotifyClassHeads)
            recipients.UnionWith(await db.ClassMemberships.AsNoTracking()
                .Where(x => x.ClassroomId == classId && x.User.Enabled)
                .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => new { x.UserId, y.Kind })
                .Where(x => x.Kind == AccountRoleKind.ClassAdministrator)
                .Select(x => x.UserId)
                .ToListAsync(ct));
        if (recipients.Count == 0) return;
        try
        {
            await notifications.PublishAsync(recipients, kind, title, body, null, ct,
                expiresAt: DateTimeOffset.UtcNow.AddHours(1), classId: classId, link: "/Devices");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "发送设备告警通知失败：{Title}", title);
        }
    }

    internal static string DeviceName(PluginCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.SoftwareInventoryJson))
        {
            try
            {
                var inventory = JsonSerializer.Deserialize<SoftwareInventory>(credential.SoftwareInventoryJson, JsonDefaults.Options);
                if (!string.IsNullOrWhiteSpace(inventory?.DeviceName)) return inventory.DeviceName;
            }
            catch (JsonException)
            {
                // 清单损坏时退回凭据名称。
            }
        }
        return string.IsNullOrWhiteSpace(credential.Name) ? "教室电脑" : credential.Name;
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1)) return "不到 1 分钟";
        if (duration < TimeSpan.FromHours(1)) return $"{(int)duration.TotalMinutes} 分钟";
        var minutes = duration.Minutes;
        return minutes == 0 ? $"{(int)duration.TotalHours} 小时" : $"{(int)duration.TotalHours} 小时 {minutes} 分钟";
    }
}

/// <summary>每分钟评估一次离线告警。</summary>
public sealed class DeviceAlertWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    ILogger<DeviceAlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    // 启动后先给插件一段重连时间，避免服务端重启瞬间把所有设备都当作离线。
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OperationsWorkers) return;
        try
        {
            await Task.Delay(StartupGrace, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<DeviceAlertEvaluator>()
                        .EvaluateAsync(timeProvider.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "评估设备离线告警失败");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }
}

/// <summary>汇总设备状态页所需数据：在线状态、版本、当前课程、音量与今日在线时间条。</summary>
public sealed class DeviceHealthService(
    AppDbContext db,
    DeviceInventoryService inventory,
    IStateStore state,
    ProfileDriftService drift,
    TimeProvider timeProvider)
{
    public async Task<DeviceHealthOverview> GetOverviewAsync(IReadOnlyList<Guid>? classScope, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var devices = (await inventory.ListAsync(ct))
            .Where(x => x.Assigned && (classScope is null || classScope.Contains(x.ClassId)))
            .ToList();
        var credentialIds = devices.Select(x => x.CredentialId).ToList();
        var groups = await db.ClassGroupAssignments.AsNoTracking()
            .Select(x => new { x.ClassroomId, x.Group.Name })
            .ToListAsync(ct);
        var alerts = await db.DeviceAlertStates.AsNoTracking()
            .Where(x => credentialIds.Contains(x.CredentialId))
            .ToDictionaryAsync(x => x.CredentialId, ct);
        var drift = await LoadDriftAsync(ct);

        // 时间条覆盖服务端本地今天 0 点至今；各设备的上下线事件按此裁剪。
        var localNow = now.ToLocalTime();
        var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        var dayStartMs = dayStart.ToUnixTimeMilliseconds();
        var events = await db.DevicePresenceEvents.AsNoTracking()
            .Where(x => credentialIds.Contains(x.CredentialId) && x.AtUnixMs >= dayStartMs)
            .OrderBy(x => x.AtUnixMs)
            .ToListAsync(ct);
        // 今天 0 点之前最后一条事件决定时间条起点的状态。
        var lastBefore = await db.DevicePresenceEvents.AsNoTracking()
            .Where(x => credentialIds.Contains(x.CredentialId) && x.AtUnixMs < dayStartMs &&
                x.AtUnixMs == db.DevicePresenceEvents
                    .Where(y => y.CredentialId == x.CredentialId && y.AtUnixMs < dayStartMs)
                    .Max(y => y.AtUnixMs))
            .ToListAsync(ct);
        var lastOffline = await db.DevicePresenceEvents.AsNoTracking()
            .Where(x => credentialIds.Contains(x.CredentialId) && !x.Online)
            .GroupBy(x => x.CredentialId)
            .Select(x => new { CredentialId = x.Key, At = x.Max(y => y.AtUnixMs) })
            .ToDictionaryAsync(x => x.CredentialId, x => x.At, ct);

        var serverVersion = VersionText.Parse(AppVersion.Version);
        var items = devices.Select(device =>
        {
            var snapshot = device.Online ? state.GetLatestSnapshot(device.ClassId) : null;
            var initiallyOnline = lastBefore.FirstOrDefault(x => x.CredentialId == device.CredentialId)?.Online ?? false;
            var timeline = BuildTimeline(
                events.Where(x => x.CredentialId == device.CredentialId).ToList(), initiallyOnline, device.Online, dayStart, now);
            DateTimeOffset? offlineSince = device.Online ? null
                : alerts.TryGetValue(device.CredentialId, out var alert) ? DateTimeOffset.FromUnixTimeMilliseconds(alert.OfflineSinceUnixMs)
                : lastOffline.TryGetValue(device.CredentialId, out var at) ? DateTimeOffset.FromUnixTimeMilliseconds(at)
                : null;
            var pluginVersion = VersionText.Parse(device.RemoteCiVersion);
            return new DeviceHealthItem(
                device.CredentialId,
                device.ClassId,
                device.ClassName,
                groups.Where(x => x.ClassroomId == device.ClassId).Select(x => x.Name).Order().ToList(),
                device.DeviceName,
                device.Online,
                device.Online ? now : offlineSince,
                offlineSince,
                device.RemoteCiVersion,
                device.ClassIslandVersion,
                pluginVersion is not null && serverVersion is not null && pluginVersion < serverVersion,
                snapshot is null ? null : DescribeCourse(snapshot),
                snapshot?.IsVolumeControlAvailable == true ? snapshot.VolumePercent : null,
                snapshot?.IsVolumeControlAvailable == true ? snapshot.IsMuted : null,
                drift.TryGetValue(device.ClassId, out var changes) ? changes : null,
                timeline);
        })
        .OrderBy(x => x.Online)
        .ThenBy(x => x.ClassName, StringComparer.CurrentCulture)
        .ThenBy(x => x.DeviceName, StringComparer.CurrentCulture)
        .ToList();
        return new DeviceHealthOverview(now, AppVersion.Version, dayStart, now, items);
    }

    /// <summary>档案差异检查结果（每班最新且仍对应当前服务端档案修订的一份）。</summary>
    private async Task<Dictionary<Guid, int>> LoadDriftAsync(CancellationToken ct) =>
        (await drift.LatestAsync(ct: ct)).ToDictionary(x => x.Key, x => x.Value.ChangeCount);

    private static IReadOnlyList<DeviceTimelineSegment> BuildTimeline(
        IReadOnlyList<DevicePresenceEvent> events, bool initiallyOnline, bool onlineNow, DateTimeOffset start, DateTimeOffset now)
    {
        var segments = new List<DeviceTimelineSegment>();
        DateTimeOffset? openedAt = initiallyOnline ? start : null;
        foreach (var change in events)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(change.AtUnixMs);
            if (change.Online) openedAt ??= at;
            else if (openedAt is { } opened)
            {
                if (at > opened) segments.Add(new DeviceTimelineSegment(opened, at));
                openedAt = null;
            }
        }
        // 事件可能因服务端重启缺失：以当前实际在线状态收尾。
        if (onlineNow) segments.Add(new DeviceTimelineSegment(openedAt ?? (segments.Count == 0 ? start : now), now));
        return segments;
    }

    private static string? DescribeCourse(ClassStateSnapshot snapshot)
    {
        var subject = string.IsNullOrWhiteSpace(snapshot.CurrentSubject) ? null : snapshot.CurrentSubject;
        return snapshot.CurrentState switch
        {
            ClassStateKind.Class => subject is null ? "上课中" : $"上课中 · {subject}",
            ClassStateKind.PrepareClass => subject is null ? "预备铃" : $"预备 · {subject}",
            ClassStateKind.AfterSchool => "已放学",
            _ when !string.IsNullOrWhiteSpace(snapshot.NextClassSubject) => $"课间 · 下节 {snapshot.NextClassSubject}",
            _ => subject,
        };
    }
}

/// <summary>从版本字符串中取出数字部分比较（忽略 v 前缀、预发布与构建元数据）。</summary>
public static class VersionText
{
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.')) end++;
        var parts = trimmed[..end].Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        // 统一补齐四段：Version 把缺失段视为 -1，"3.3.0" 会被误判为低于 "3.3.0.0"。
        while (parts.Length < 4) parts = [.. parts, "0"];
        return Version.TryParse(string.Join('.', parts.Take(4)), out var version) ? version : null;
    }
}
