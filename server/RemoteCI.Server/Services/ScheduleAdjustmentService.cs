using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>要写入的一节调课：BaseSubjectId 为该节基础课表的科目（已有调课时以已有记录的守卫为准）。</summary>
public sealed record PlannedSlotChange(
    Guid ClassId,
    string Date,
    int Index,
    Guid BaseSubjectId,
    string BaseSubject,
    Guid ToSubjectId,
    string ToSubject,
    string? TeacherName);

/// <summary>写入调课后的结果：新记录与各班新的调课集合版本（用于等待插件回执）。</summary>
public sealed record AdjustmentWrite(IReadOnlyList<ScheduleAdjustment> Rows, IReadOnlyDictionary<Guid, long> Versions);

/// <summary>对账结果：因教室端冲突而失效的调课所属的换课申请。</summary>
public sealed record AdjustmentReconcileOutcome(IReadOnlyList<(Guid SwapRequestId, string Reason)> Invalidated);

/// <summary>
/// 调课记录：单日调整的权威来源。写入后递增班级调课集合版本并下发给教室端插件，插件按“基础课表 + 调课”
/// 生成托管临时层，在课表推送中回执；这里据回执更新送达状态、收编插件本地调课、把冲突记录置为失效。
/// 集合版本与叠放序号跨请求共享，写操作经静态锁串行；下发在锁外进行，测试替身可在下发时同步回执。
/// </summary>
public sealed class ScheduleAdjustmentService(
    AppDbContext db,
    IStateStore state,
    ScheduleAdjustmentOverlay overlay,
    IScheduleAdjustmentChannel channel,
    ScheduleAdjustmentAckWaiter acks,
    TimeProvider time,
    ILogger<ScheduleAdjustmentService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>插件回执版本落后时补发的最短间隔，避免每次课表推送都重发。</summary>
    private static readonly TimeSpan LagRepushInterval = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, long> LastLagRepush = new();

    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(8);

    // ---------- 写入 ----------

    public async Task<AdjustmentWrite> AddAsync(IReadOnlyList<PlannedSlotChange> changes, ScheduleAdjustmentOrigin origin,
        Guid? swapRequestId, Guid? userId, string userName, CancellationToken ct = default)
    {
        var rows = new List<ScheduleAdjustment>();
        var versions = new Dictionary<Guid, long>();
        await Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var change in changes)
            {
                var sync = await SyncStateAsync(change.ClassId, ct);
                var guard = await TopAsync(change.ClassId, change.Date, change.Index, ct);
                var row = new ScheduleAdjustment
                {
                    Id = Guid.NewGuid(),
                    ClassId = change.ClassId,
                    Date = change.Date,
                    Index = change.Index,
                    Sequence = ++sync.AdjustmentSequence,
                    FromSubjectId = guard?.FromSubjectId ?? change.BaseSubjectId,
                    FromSubject = guard?.FromSubject ?? change.BaseSubject,
                    ToSubjectId = change.ToSubjectId,
                    ToSubject = change.ToSubject,
                    TeacherName = string.IsNullOrWhiteSpace(change.TeacherName) ? null : change.TeacherName.Trim(),
                    Origin = origin,
                    SwapRequestId = swapRequestId,
                    CreatedByUserId = userId,
                    CreatedByName = Limit(userName, 100),
                    CreatedAtUnixMs = now,
                    Status = ScheduleAdjustmentStatus.Active,
                    Delivery = ScheduleAdjustmentDelivery.Pending,
                };
                db.ScheduleAdjustments.Add(row);
                rows.Add(row);
                if (!versions.ContainsKey(change.ClassId)) versions[change.ClassId] = ++sync.AdjustmentVersion;
            }
            await db.SaveChangesAsync(ct);
            await RefreshOverlayAsync(versions.Keys, ct);
        }
        finally
        {
            Gate.Release();
        }
        foreach (var classId in versions.Keys) await PushAsync(classId, ct);
        return new AdjustmentWrite(rows, versions);
    }

    /// <summary>撤回换课申请产生的全部有效调课；返回各班新版本。</summary>
    public Task<IReadOnlyDictionary<Guid, long>> RevertBySwapAsync(Guid swapRequestId, string reason, CancellationToken ct = default) =>
        CloseAsync(x => x.SwapRequestId == swapRequestId, ScheduleAdjustmentStatus.Reverted, null, reason, ct);

    /// <summary>撤回（删除）单条调课记录。</summary>
    public Task<IReadOnlyDictionary<Guid, long>> RevertAsync(Guid id, string reason, CancellationToken ct = default) =>
        CloseAsync(x => x.Id == id, ScheduleAdjustmentStatus.Reverted, null, reason, ct);

    /// <summary>把换课申请的其余有效调课置为失效（另一半已冲突时，不能只留半个换课）。</summary>
    public Task<IReadOnlyDictionary<Guid, long>> InvalidateBySwapAsync(Guid swapRequestId, string? code, string reason, CancellationToken ct = default) =>
        CloseAsync(x => x.SwapRequestId == swapRequestId, ScheduleAdjustmentStatus.Invalidated, code, reason, ct);

    /// <summary>插件过旧，无法按调课生成临时层：该班尚未送达的有效调课全部失效。</summary>
    public async Task<AdjustmentReconcileOutcome> InvalidatePendingForOutdatedPluginAsync(Guid classId, CancellationToken ct = default)
    {
        var rows = await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.ClassId == classId && x.Status == ScheduleAdjustmentStatus.Active && x.Delivery == ScheduleAdjustmentDelivery.Pending)
            .Select(x => new { x.Id, x.SwapRequestId }).ToListAsync(ct);
        if (rows.Count == 0) return new AdjustmentReconcileOutcome([]);
        var ids = rows.Select(x => x.Id).ToHashSet();
        var reason = ScheduleAdjustmentConflictCodes.Describe(ScheduleAdjustmentConflictCodes.PluginOutdated) + "，请升级 RemoteCI 插件";
        await CloseAsync(x => ids.Contains(x.Id), ScheduleAdjustmentStatus.Invalidated, ScheduleAdjustmentConflictCodes.PluginOutdated, reason, ct);
        return new AdjustmentReconcileOutcome(rows.Where(x => x.SwapRequestId is not null)
            .Select(x => (x.SwapRequestId!.Value, reason)).Distinct().ToList());
    }

    private async Task<IReadOnlyDictionary<Guid, long>> CloseAsync(
        System.Linq.Expressions.Expression<Func<ScheduleAdjustment, bool>> filter,
        ScheduleAdjustmentStatus status, string? code, string reason, CancellationToken ct)
    {
        var versions = new Dictionary<Guid, long>();
        await Gate.WaitAsync(ct);
        try
        {
            var rows = await db.ScheduleAdjustments.Where(filter).Where(x => x.Status == ScheduleAdjustmentStatus.Active).ToListAsync(ct);
            if (rows.Count == 0) return versions;
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var row in rows)
            {
                Close(row, status, code, reason, now);
                if (!versions.ContainsKey(row.ClassId)) versions[row.ClassId] = ++(await SyncStateAsync(row.ClassId, ct)).AdjustmentVersion;
            }
            await db.SaveChangesAsync(ct);
            await RefreshOverlayAsync(versions.Keys, ct);
        }
        finally
        {
            Gate.Release();
        }
        foreach (var classId in versions.Keys) await PushAsync(classId, ct);
        return versions;
    }

    // ---------- 下发与回执 ----------

    /// <summary>该班今天起的有效调课（每课位只取最上层）与已收编的本地调课 ID。</summary>
    public async Task<ScheduleAdjustmentSet> BuildSetAsync(Guid classId, CancellationToken ct = default)
    {
        var today = DateKey(ClassClock.Today(state, classId));
        var rows = await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.ClassId == classId && string.Compare(x.Date, today) >= 0)
            .ToListAsync(ct);
        var version = await db.ClassSyncStates.AsNoTracking().Where(x => x.ClassId == classId)
            .Select(x => x.AdjustmentVersion).SingleOrDefaultAsync(ct);
        return new ScheduleAdjustmentSet
        {
            ClassId = classId,
            Version = version,
            GeneratedAt = time.GetUtcNow(),
            Items = Effective(rows).OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Index).Select(x => new ScheduleSlotChange
            {
                Id = x.Id,
                Date = x.Date,
                Index = x.Index,
                FromSubjectId = x.FromSubjectId,
                ToSubjectId = x.ToSubjectId,
            }).ToList(),
            AcknowledgedLocalIds = rows.Where(x => x.Origin is ScheduleAdjustmentOrigin.Direct or ScheduleAdjustmentOrigin.ClassIsland)
                .Select(x => x.Id).ToList(),
        };
    }

    /// <summary>向在线且支持调课集合的插件下发；离线或旧插件时什么也不做（上线后按能力补发）。</summary>
    public async Task<bool> PushAsync(Guid classId, CancellationToken ct = default)
    {
        if (channel.Supports(classId) != true) return false;
        try
        {
            return await channel.PushAsync(classId, await BuildSetAsync(classId, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "向班级 {ClassId} 下发调课集合失败", classId);
            return false;
        }
    }

    /// <summary>等待各班插件回执不低于指定版本；全部回执返回 true。离线或不支持的班级不等待，视为未回执。</summary>
    public async Task<bool> WaitForAcksAsync(IReadOnlyDictionary<Guid, long> versions, CancellationToken ct = default)
    {
        var all = true;
        foreach (var (classId, version) in versions)
        {
            if (channel.Supports(classId) != true) { all = false; continue; }
            all &= await acks.WaitAsync(classId, version, AckTimeout, ct);
        }
        return all;
    }

    /// <summary>
    /// 处理插件随课表上报的调课回执：按 ID 幂等收编本地调课，更新送达状态，把冲突的记录（同一课位共享守卫的全部记录）
    /// 置为失效，记录回执版本并唤醒等待者；有变化或插件版本落后时重新下发。
    /// </summary>
    public async Task<AdjustmentReconcileOutcome> ReconcileAsync(Guid classId, ScheduleAdjustmentReport report, ScheduleBundle bundle,
        CancellationToken ct = default)
    {
        var invalidated = new List<(Guid, string)>();
        var changed = false;
        long version;
        await Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var sync = await SyncStateAsync(classId, ct);
            changed |= await AdoptLocalAsync(classId, report.Local, bundle, sync, now, ct);

            var resultIds = report.Items.Select(x => x.Id).ToList();
            var reported = await db.ScheduleAdjustments
                .Where(x => x.ClassId == classId && resultIds.Contains(x.Id) && x.Status == ScheduleAdjustmentStatus.Active)
                .ToDictionaryAsync(x => x.Id, ct);
            foreach (var result in report.Items)
            {
                if (!reported.TryGetValue(result.Id, out var row) || row.Status != ScheduleAdjustmentStatus.Active) continue;
                if (result.State == ScheduleAdjustmentItemState.Applied)
                {
                    row.Delivery = ScheduleAdjustmentDelivery.Applied;
                    continue;
                }
                if (result.State != ScheduleAdjustmentItemState.Conflict) continue;
                var code = Limit(result.Code ?? "CONFLICT", 40);
                var reason = Limit(result.Message ?? ScheduleAdjustmentConflictCodes.Describe(result.Code), 200);
                // 同一课位的记录共享守卫，最上层冲突意味着整叠都无法再应用。
                var stack = await db.ScheduleAdjustments.Where(x => x.ClassId == classId && x.Date == row.Date && x.Index == row.Index &&
                    x.Status == ScheduleAdjustmentStatus.Active && x.FromSubjectId == row.FromSubjectId).ToListAsync(ct);
                foreach (var item in stack)
                {
                    Close(item, ScheduleAdjustmentStatus.Invalidated, code, reason, now);
                    item.Delivery = ScheduleAdjustmentDelivery.Conflict;
                    if (item.SwapRequestId is { } swapId) invalidated.Add((swapId, reason));
                }
                changed = true;
            }
            if (report.Version > sync.AckedAdjustmentVersion)
            {
                sync.AckedAdjustmentVersion = report.Version;
                sync.AckedAtUnixMs = now;
            }
            if (changed) sync.AdjustmentVersion++;
            version = sync.AdjustmentVersion;
            await db.SaveChangesAsync(ct);
            await RefreshOverlayAsync([classId], ct);
        }
        finally
        {
            Gate.Release();
        }
        acks.Acknowledge(classId, report.Version);
        var lagging = report.Version < version;
        if (changed || lagging && ShouldRepushLag(classId)) await PushAsync(classId, ct);
        return new AdjustmentReconcileOutcome(invalidated.Distinct().ToList());
    }

    private async Task<bool> AdoptLocalAsync(Guid classId, IReadOnlyList<LocalScheduleAdjustment> local, ScheduleBundle bundle,
        ClassSyncState sync, long now, CancellationToken ct)
    {
        if (local.Count == 0) return false;
        var ids = local.Select(x => x.Id).ToList();
        var known = (await db.ScheduleAdjustments.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var subjects = bundle.Subjects.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First().Name);
        var added = false;
        foreach (var item in local.Where(x => !known.Contains(x.Id) && x.Index >= 0 && IsDate(x.Date)).OrderBy(x => x.CreatedAt))
        {
            db.ScheduleAdjustments.Add(new ScheduleAdjustment
            {
                Id = item.Id,
                ClassId = classId,
                Date = item.Date,
                Index = item.Index,
                Sequence = ++sync.AdjustmentSequence,
                FromSubjectId = item.FromSubjectId,
                FromSubject = Limit(subjects.GetValueOrDefault(item.FromSubjectId) ?? "未设置", 100),
                ToSubjectId = item.ToSubjectId,
                ToSubject = Limit(subjects.GetValueOrDefault(item.ToSubjectId) ?? "未设置", 100),
                Origin = item.Origin == ScheduleAdjustmentOrigins.ClassIsland ? ScheduleAdjustmentOrigin.ClassIsland : ScheduleAdjustmentOrigin.Direct,
                CreatedByUserId = item.RequestedByUserId,
                CreatedByName = Limit(string.IsNullOrWhiteSpace(item.RequestedByName) ? "教室端" : item.RequestedByName.Trim(), 100),
                CreatedAtUnixMs = Math.Min(now, item.CreatedAt.ToUnixTimeMilliseconds()),
                Status = ScheduleAdjustmentStatus.Active,
                Delivery = ScheduleAdjustmentDelivery.Applied,
            });
            added = true;
        }
        return added;
    }

    private static bool ShouldRepushLag(Guid classId)
    {
        var now = Environment.TickCount64;
        var last = LastLagRepush.GetOrAdd(classId, long.MinValue);
        if (last != long.MinValue && now - last < (long)LagRepushInterval.TotalMilliseconds) return false;
        LastLagRepush[classId] = now;
        return true;
    }

    // ---------- 查询、清扫与启动 ----------

    public async Task<IReadOnlyList<ScheduleAdjustmentView>> ListAsync(Guid classId, string? from, CancellationToken ct = default)
    {
        var start = from is not null && IsDate(from) ? from : DateKey(ClassClock.Today(state, classId));
        var rows = await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.ClassId == classId && string.Compare(x.Date, start) >= 0)
            .ToListAsync(ct);
        var effective = Effective(rows).Select(x => x.Id).ToHashSet();
        return rows.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Index).ThenBy(x => x.Sequence)
            .Select(x => ToView(x, effective.Contains(x.Id))).ToList();
    }

    public Task<ScheduleAdjustment?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.ScheduleAdjustments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    /// <summary>日期已过的有效调课标记为过期。</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var active = await db.ScheduleAdjustments.Where(x => x.Status == ScheduleAdjustmentStatus.Active).ToListAsync(ct);
        var stale = active.Where(x => string.CompareOrdinal(x.Date, DateKey(ClassClock.Today(state, x.ClassId))) < 0).ToList();
        if (stale.Count == 0) return;
        await Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var row in stale) Close(row, ScheduleAdjustmentStatus.Expired, null, "日期已过", now);
            await db.SaveChangesAsync(ct);
            await RefreshOverlayAsync(stale.Select(x => x.ClassId).Distinct().ToList(), ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>启动时把有效调课载入内存叠加表；须在接受连接前完成，首个课表推送即可正确叠加。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var rows = await db.ScheduleAdjustments.AsNoTracking().Where(x => x.Status == ScheduleAdjustmentStatus.Active).ToListAsync(ct);
        overlay.ReplaceAll(Effective(rows).Select(ToEntry));
    }

    /// <summary>
    /// 升级回填：把旧版逐节命令记录在 AppliedPlanJson 中、今天及以后仍有效的换课转成调课记录（按申请幂等）。
    /// 同一课位多次换课时以最早一次的原科目作为基础科目守卫；科目按名称从缓存课表解析，解析不到的跳过。
    /// </summary>
    public async Task<int> BackfillAsync(CancellationToken ct = default)
    {
        var swaps = await db.ScheduleSwapRequests.AsNoTracking()
            .Where(x => (x.Status == SwapRequestStatus.Approved || x.Status == SwapRequestStatus.Forced) && x.AppliedPlanJson != null)
            .ToListAsync(ct);
        if (swaps.Count == 0) return 0;
        var done = (await db.ScheduleAdjustments.AsNoTracking().Where(x => x.SwapRequestId != null)
            .Select(x => x.SwapRequestId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var planned = new List<(ScheduleSwapRequest Swap, List<PlannedSlotChange> Changes)>();
        var guards = new Dictionary<(Guid, string, int), (Guid Id, string Name)>();
        foreach (var swap in swaps.Where(x => !done.Contains(x.Id)).OrderBy(x => x.DecidedAt ?? x.CreatedAt))
        {
            List<ScheduleSwapService.AppliedChange> applied;
            try { applied = JsonSerializer.Deserialize<List<ScheduleSwapService.AppliedChange>>(swap.AppliedPlanJson!) ?? []; }
            catch (JsonException) { continue; }
            var changes = new List<PlannedSlotChange>();
            foreach (var change in applied)
            {
                if (string.CompareOrdinal(change.Date, DateKey(ClassClock.Today(state, change.ClassId))) < 0) continue;
                var subject = state.GetSourceSchedule(change.ClassId)?.Subjects
                    .FirstOrDefault(x => string.Equals(x.Name, change.ToSubject, StringComparison.Ordinal));
                if (subject is null) continue;
                var key = (change.ClassId, change.Date, change.Index);
                if (!guards.TryGetValue(key, out var guard)) guards[key] = guard = (change.FromSubjectId, change.FromSubject);
                changes.Add(new PlannedSlotChange(change.ClassId, change.Date, change.Index, guard.Id, guard.Name,
                    subject.Id, subject.Name, change.NewOverrideTeacher));
            }
            if (changes.Count > 0) planned.Add((swap, changes));
        }
        foreach (var (swap, changes) in planned)
        {
            await AddAsync(changes, ScheduleAdjustmentOrigin.Backfill, swap.Id, swap.DecidedByUserId ?? swap.RequesterUserId, "升级迁移", ct);
            // 旧版换课已经在教室端生效，回填记录不必等待回执；插件接管旧临时层后会再回执。
            await db.ScheduleAdjustments.Where(x => x.SwapRequestId == swap.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Delivery, ScheduleAdjustmentDelivery.Applied), ct);
        }
        if (planned.Count > 0) logger.LogInformation("已把 {Count} 条旧换课回填为调课记录", planned.Count);
        return planned.Count;
    }

    // ---------- 辅助 ----------

    /// <summary>每个课位叠放最上层的有效记录。</summary>
    public static IEnumerable<ScheduleAdjustment> Effective(IEnumerable<ScheduleAdjustment> rows) =>
        rows.Where(x => x.Status == ScheduleAdjustmentStatus.Active)
            .GroupBy(x => (x.ClassId, x.Date, x.Index))
            .Select(group => group.MaxBy(x => x.Sequence)!);

    private async Task<ScheduleAdjustment?> TopAsync(Guid classId, string date, int index, CancellationToken ct)
    {
        var local = db.ScheduleAdjustments.Local.Where(x => x.ClassId == classId && x.Date == date && x.Index == index &&
            x.Status == ScheduleAdjustmentStatus.Active).MaxBy(x => x.Sequence);
        var stored = await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.ClassId == classId && x.Date == date && x.Index == index && x.Status == ScheduleAdjustmentStatus.Active)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        return local is null ? stored : stored is null || local.Sequence > stored.Sequence ? local : stored;
    }

    private async Task<ClassSyncState> SyncStateAsync(Guid classId, CancellationToken ct)
    {
        var sync = db.ClassSyncStates.Local.FirstOrDefault(x => x.ClassId == classId)
            ?? await db.ClassSyncStates.SingleOrDefaultAsync(x => x.ClassId == classId, ct);
        if (sync is not null) return sync;
        sync = new ClassSyncState { ClassId = classId };
        db.ClassSyncStates.Add(sync);
        return sync;
    }

    private async Task RefreshOverlayAsync(IEnumerable<Guid> classIds, CancellationToken ct)
    {
        foreach (var classId in classIds.Distinct())
        {
            var rows = await db.ScheduleAdjustments.AsNoTracking()
                .Where(x => x.ClassId == classId && x.Status == ScheduleAdjustmentStatus.Active).ToListAsync(ct);
            overlay.ReplaceClass(classId, Effective(rows).Select(ToEntry));
        }
    }

    private static void Close(ScheduleAdjustment row, ScheduleAdjustmentStatus status, string? code, string reason, long now)
    {
        row.Status = status;
        row.ConflictCode = code;
        row.StatusReason = Limit(reason, 200);
        row.ClosedAtUnixMs = now;
    }

    private static AdjustmentOverlayEntry ToEntry(ScheduleAdjustment x) =>
        new(x.Id, x.ClassId, x.Date, x.Index, x.FromSubjectId, x.ToSubjectId, x.ToSubject, x.TeacherName);

    private static ScheduleAdjustmentView ToView(ScheduleAdjustment x, bool effective) => new()
    {
        Id = x.Id,
        ClassId = x.ClassId,
        Date = x.Date,
        Index = x.Index,
        FromSubject = x.FromSubject,
        ToSubject = x.ToSubject,
        Teacher = x.TeacherName,
        Origin = x.Origin switch
        {
            ScheduleAdjustmentOrigin.SwapRequest => "swap",
            ScheduleAdjustmentOrigin.Direct => "direct",
            ScheduleAdjustmentOrigin.ClassIsland => "classisland",
            _ => "backfill",
        },
        Status = x.Status.ToString().ToLowerInvariant(),
        Delivery = x.Delivery.ToString().ToLowerInvariant(),
        Effective = effective,
        ConflictCode = x.ConflictCode,
        Reason = x.StatusReason,
        SwapRequestId = x.SwapRequestId,
        CreatedByName = x.CreatedByName,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtUnixMs),
    };

    private static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}
