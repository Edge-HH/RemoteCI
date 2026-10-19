using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed record ProfileDriftView(
    Guid ClassId,
    string ClassName,
    DateTimeOffset CheckedAt,
    int ChangeCount,
    IReadOnlyList<ProfileChange> Changes);

public sealed record ProfileDriftCheckResult(Guid ClassId, string ClassName, bool Success, string Message, int? ChangeCount);

/// <summary>
/// 档案差异检查：读取教室电脑当前档案，与服务端班级档案比较时间表、课表与科目（不含临时层）。
/// 结果只记录差异，不自动覆盖任何一方；处理方式（以设备为准载入草稿 / 以服务端为准下发）仍由人决定。
/// </summary>
public sealed class ProfileDriftService(
    AppDbContext db,
    ProfileDispatchService dispatch,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ProfileDriftCheckResult>> CheckAsync(
        AppUser actor, IReadOnlyList<Guid> classIds, Guid? onlyClass = null, CancellationToken ct = default)
    {
        var stored = await db.StoredProfiles.AsNoTracking()
            .Where(x => x.ClassId != null && classIds.Contains(x.ClassId.Value))
            .ToDictionaryAsync(x => x.ClassId!.Value, ct);
        var targets = classIds.Where(stored.ContainsKey).Distinct().ToList();
        var results = classIds.Where(x => !stored.ContainsKey(x)).Distinct()
            .Select(x => new ProfileDriftCheckResult(x, string.Empty, false, "该班还没有服务端档案，无法比较。", null))
            .ToList();
        if (targets.Count == 0) return results;

        IReadOnlyList<ProfileCollectResult> collected;
        using (CommandAuditContext.Begin(CommandAuditSource.DriftCheck))
            collected = await dispatch.CollectAsync(actor, new ProfileCollectRequest { ClassIds = targets }, onlyClass, ct);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var item in collected)
        {
            if (!item.Success || item.ProfileJson is null)
            {
                results.Add(new ProfileDriftCheckResult(item.ClassId, item.ClassName, false, item.Message, null));
                continue;
            }
            var profile = stored[item.ClassId];
            var changes = ProfileLibraryService.SafeDiff(profile.ProfileJson, item.ProfileJson, includeTempLayers: false);
            var report = await db.ProfileDriftReports.SingleOrDefaultAsync(x => x.ClassId == item.ClassId, ct);
            if (report is null)
            {
                report = new ProfileDriftReport { ClassId = item.ClassId };
                db.ProfileDriftReports.Add(report);
            }
            report.CheckedAtUnixMs = now;
            report.ProfileRevision = profile.Revision;
            report.ChangeCount = changes.Count;
            report.ChangesJson = JsonSerializer.Serialize(changes, JsonDefaults.Options);
            results.Add(new ProfileDriftCheckResult(item.ClassId, item.ClassName, true,
                changes.Count == 0 ? "设备档案与服务端一致。" : $"发现 {changes.Count} 处差异。", changes.Count));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>各班最近一次结果；服务端档案在比较后又保存过时，旧结果不再有效，不返回。</summary>
    public async Task<IReadOnlyDictionary<Guid, ProfileDriftView>> LatestAsync(
        IReadOnlyCollection<Guid>? classIds = null, CancellationToken ct = default)
    {
        var query = db.ProfileDriftReports.AsNoTracking();
        if (classIds is not null)
        {
            var wanted = classIds.ToList();
            query = query.Where(x => wanted.Contains(x.ClassId));
        }
        var reports = await query.ToListAsync(ct);
        if (reports.Count == 0) return new Dictionary<Guid, ProfileDriftView>();
        var ids = reports.Select(x => x.ClassId).ToList();
        var revisions = await db.StoredProfiles.AsNoTracking()
            .Where(x => x.ClassId != null && ids.Contains(x.ClassId.Value))
            .ToDictionaryAsync(x => x.ClassId!.Value, x => x.Revision, ct);
        var names = await db.Classrooms.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return reports
            .Where(x => revisions.TryGetValue(x.ClassId, out var revision) && revision == x.ProfileRevision)
            .ToDictionary(x => x.ClassId, x => new ProfileDriftView(
                x.ClassId,
                names.GetValueOrDefault(x.ClassId) ?? "未知班级",
                DateTimeOffset.FromUnixTimeMilliseconds(x.CheckedAtUnixMs),
                x.ChangeCount,
                JsonSerializer.Deserialize<List<ProfileChange>>(x.ChangesJson, JsonDefaults.Options) ?? []));
    }
}

/// <summary>按系统配置的时刻每天检查一次全部在线班级的档案差异，以第一位启用的系统管理员身份读取。</summary>
public sealed class ProfileDriftWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    ILogger<ProfileDriftWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OperationsWorkers) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RunIfDueAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "每日档案差异检查失败");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metadata = await db.SystemMetadata.SingleOrDefaultAsync(ct);
        if (metadata is null || !metadata.ProfileDriftCheckEnabled) return;
        var now = timeProvider.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (metadata.ProfileDriftLastCheckDate == today ||
            !TimeOnly.TryParseExact(metadata.ProfileDriftCheckTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ||
            TimeOnly.FromDateTime(now.DateTime) < at) return;
        metadata.ProfileDriftLastCheckDate = today;
        await db.SaveChangesAsync(ct);

        var actor = await db.Users.Where(x => x.Role == UserRole.Admin && x.Enabled)
            .OrderByDescending(x => x.IsSystemOwner).FirstOrDefaultAsync(ct);
        if (actor is null) return;
        var online = scope.ServiceProvider.GetRequiredService<PeerRegistry>().GetOnlinePluginConnections()
            .Select(x => x.ClassId).Distinct().ToList();
        var classIds = await db.StoredProfiles.AsNoTracking()
            .Where(x => x.ClassId != null && online.Contains(x.ClassId.Value))
            .Select(x => x.ClassId!.Value).ToListAsync(ct);
        if (classIds.Count == 0) return;
        var drift = scope.ServiceProvider.GetRequiredService<ProfileDriftService>();
        var results = new List<ProfileDriftCheckResult>();
        // 收集接口每批最多 100 个班级。
        foreach (var chunk in classIds.Chunk(100))
            results.AddRange(await drift.CheckAsync(actor, chunk, ct: ct));
        logger.LogInformation("每日档案差异检查完成：{Checked} 个班级，{Drift} 个存在差异",
            results.Count(x => x.Success), results.Count(x => x.ChangeCount > 0));
    }
}
