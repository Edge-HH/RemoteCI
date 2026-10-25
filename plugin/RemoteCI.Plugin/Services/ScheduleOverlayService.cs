using System.Globalization;
using System.Text.Json;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>课表上报读取托管临时层信息的接口：基础科目与生效调课、调课回执。</summary>
public interface IScheduleOverlayInfo
{
    ScheduleAdjustmentReport BuildReport();
    bool TryGetDate(string date, out IReadOnlyList<Guid> baseSubjects, out IReadOnlyDictionary<int, Guid> appliedIds);
}

/// <summary>
/// 托管临时层：按“基础课表（调休上学日为所跟星期那天的课表）+ 服务端调课 + 本地调课”生成每天的 ClassIsland 临时层，
/// 随时可以重建，见 docs/adr/0001-timetable-source-of-truth.md。
/// <list type="bullet">
/// <item>每条调课带守卫（该节基础科目），对不上即报告冲突，由服务端置为失效并通知老师，而不是静默套到别的节次上。</item>
/// <item>RemoteCI 的临时换课（课表页、手机、手表、局域网）转为本地调课；在 ClassIsland 里直接改托管临时层，按差异转为本地调课。
/// 本地调课随课表上报，服务端收编后在调课集合中确认。</item>
/// <item>外来临时层（档案下发、ClassIsland 自带换课、升级前遗留）与基础课表节数相同时接管（保留其时间表），否则该日不托管。</item>
/// <item>临时层由插件建立的原地改写，课表 ID 不变，修订号只在内容变化时变化；基础课表时间表变化时重建。</item>
/// </list>
/// 只能在 UI 线程调用。
/// </summary>
public sealed class ScheduleOverlayService(
    IScheduleOverlayHost host,
    ScheduleOverlayStateFile stateFile,
    IHolidayCalendarLookup? holidays,
    ILogger<ScheduleOverlayService> logger) : IScheduleOverlayInfo
{
    /// <summary>调休上学日与接管外来临时层的前瞻天数（与七日课表一致）。</summary>
    public const int Horizon = 7;

    private readonly Dictionary<Guid, ScheduleAdjustmentItemResult> _results = [];
    private readonly HashSet<string> _unmanaged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (List<Guid> Base, IReadOnlyDictionary<int, Guid> Applied)> _composed = new(StringComparer.Ordinal);

    private ScheduleOverlayStateFile.StateDocument State => stateFile.Document;

    /// <summary>应用服务端调课集合（期望状态）；返回 ClassIsland 课表是否有变化。旧版本的集合直接忽略。</summary>
    public bool ApplyServerSet(ScheduleAdjustmentSet set, DateTime now)
    {
        if (State.ServerManaged && set.Version < State.ServerVersion) return false;
        State.ServerManaged = true;
        State.ServerVersion = set.Version;
        State.ServerItems = set.Items.Where(x => TryParseDate(x.Date, out _)).ToList();
        var acknowledged = set.AcknowledgedLocalIds.ToHashSet();
        State.PendingLocal.RemoveAll(x => acknowledged.Contains(x.Id));
        _results.Clear();
        var changed = Run(now, full: true, out _);
        stateFile.Save();
        return changed;
    }

    /// <summary>重新解析基础课表并重建全部托管临时层（调休、档案应用、学期、跨天之后）。</summary>
    public bool Recompose(DateTime now) => Run(now, full: true, out _);

    /// <summary>定时核对：发现托管临时层丢失、被改或出现外来临时层时处理；沿用记录的基础课表，不重新解析。</summary>
    public bool Verify(DateTime now) => Run(now, full: false, out _);

    /// <summary>
    /// 把一次临时换课转为本地调课并生成临时层。该日无法托管（没有课表、外来临时层节数不同）时返回 null，
    /// 由调用方按旧方式直接修改当天课表。前置的修订号校验由调用方完成。
    /// </summary>
    public CommandResult? TryApplyLocalChange(DateTime date, ScheduleChangeRequest request, UserProfile? requestedBy, DateTime now)
    {
        var key = Key(date);
        var context = new RunContext(now.Date);
        ComposeDate(key, full: true, context, force: true);
        if (!_composed.TryGetValue(key, out var composed) || State.Dates.GetValueOrDefault(key)?.Expected is not { } effective)
        {
            Flush(context);
            return null;
        }
        if (ScheduleMutation.Validate(effective.Count, request, host.SubjectExists) is { } error)
        {
            Flush(context);
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        }
        var added = ScheduleAdjustmentMath.FromChangeRequest(request, effective, composed.Base).Select(change => new LocalScheduleAdjustment
        {
            Id = Guid.NewGuid(),
            Date = key,
            Index = change.Index,
            FromSubjectId = change.From,
            ToSubjectId = change.To,
            Origin = ScheduleAdjustmentOrigins.Direct,
            RequestedByUserId = requestedBy?.Id,
            RequestedByName = string.IsNullOrWhiteSpace(requestedBy?.DisplayName) ? requestedBy?.Username : requestedBy.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        State.PendingLocal.AddRange(added);
        ComposeDate(key, full: false, context, force: true);
        try
        {
            if (context.HostChanged) host.SaveProfile();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 {Date} 临时层失败，已撤销本次换课", key);
            var ids = added.Select(x => x.Id).ToHashSet();
            State.PendingLocal.RemoveAll(x => ids.Contains(x.Id));
            ComposeDate(key, full: false, new RunContext(now.Date), force: true);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, "ClassIsland 保存课表失败，操作未确认");
        }
        stateFile.Save();
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = request.Mode == ScheduleChangeMode.Exchange ? "两节课程已临时交换" : "课程已临时替换",
        };
    }

    public ScheduleAdjustmentReport BuildReport() => new()
    {
        Version = State.ServerVersion,
        Items = State.ServerItems.Select(x => _results.TryGetValue(x.Id, out var result)
            ? result
            : new ScheduleAdjustmentItemResult { Id = x.Id, State = ScheduleAdjustmentItemState.Pending }).ToList(),
        Local = [.. State.PendingLocal],
        UnmanagedDates = _unmanaged.Order(StringComparer.Ordinal).ToList(),
    };

    public bool TryGetDate(string date, out IReadOnlyList<Guid> baseSubjects, out IReadOnlyDictionary<int, Guid> appliedIds)
    {
        if (_composed.TryGetValue(date, out var composed))
        {
            baseSubjects = composed.Base;
            appliedIds = composed.Applied;
            return true;
        }
        baseSubjects = [];
        appliedIds = new Dictionary<int, Guid>();
        return false;
    }

    // ---------- 组合 ----------

    private sealed class RunContext(DateTime today)
    {
        public DateTime Today { get; } = today;
        public bool HostChanged { get; set; }
        public bool StateChanged { get; set; }
    }

    private bool Run(DateTime now, bool full, out bool stateChanged)
    {
        var context = new RunContext(now.Date);
        context.StateChanged = Prune(context.Today);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in State.ServerItems) keys.Add(item.Date);
        foreach (var item in State.PendingLocal) keys.Add(item.Date);
        foreach (var key in State.Dates.Keys) keys.Add(key);
        for (var offset = 0; offset < Horizon; offset++) keys.Add(Key(context.Today.AddDays(offset)));
        foreach (var key in keys.Where(x => string.CompareOrdinal(x, Key(context.Today)) >= 0))
        {
            try
            {
                ComposeDate(key, full, context, force: false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "生成 {Date} 托管临时层失败", key);
            }
        }
        Flush(context);
        stateChanged = context.StateChanged;
        return context.HostChanged;
    }

    private void Flush(RunContext context)
    {
        if (context.HostChanged)
        {
            try
            {
                host.SaveProfile();
            }
            catch (Exception ex)
            {
                // 内存中的档案已更新，宿主之后保存时会一并写入；下一轮核对会再尝试。
                logger.LogError(ex, "保存托管临时层失败");
            }
        }
        if (context.HostChanged || context.StateChanged) stateFile.Save();
    }

    private bool Prune(DateTime today)
    {
        var todayKey = Key(today);
        var removed = State.ServerItems.RemoveAll(x => string.CompareOrdinal(x.Date, todayKey) < 0) > 0;
        removed |= State.PendingLocal.RemoveAll(x => string.CompareOrdinal(x.Date, todayKey) < 0) > 0;
        // 过去日期的临时层由 ClassIsland 自己的过期清理处理，这里只忘掉记录。
        foreach (var key in State.Dates.Keys.Where(x => string.CompareOrdinal(x, todayKey) < 0).ToList())
        {
            State.Dates.Remove(key);
            removed = true;
        }
        foreach (var key in _composed.Keys.Where(x => string.CompareOrdinal(x, todayKey) < 0).ToList()) _composed.Remove(key);
        _unmanaged.RemoveWhere(x => string.CompareOrdinal(x, todayKey) < 0);
        return removed;
    }

    private void ComposeDate(string key, bool full, RunContext context, bool force)
    {
        if (!TryParseDate(key, out var date)) return;
        var holiday = holidays?.Find(date);
        var serverItems = State.ServerItems.Where(x => x.Date == key).ToList();
        var locals = State.PendingLocal.Where(x => x.Date == key).ToList();
        State.Dates.TryGetValue(key, out var managed);
        _unmanaged.Remove(key);
        _composed.Remove(key);

        if (holiday?.Kind == HolidayDayKinds.Off)
        {
            Conflict(serverItems, ScheduleAdjustmentConflictCodes.Holiday);
            if (managed is not null) Release(date, key, managed, null, context);
            return;
        }

        int? makeup = holiday?.Kind == HolidayDayKinds.Makeup ? holiday.FollowWeekday : null;
        var orderedId = host.GetOrderedPlanId(date);
        var orderedPlan = orderedId is { } id ? host.ClassPlans.GetValueOrDefault(id) : null;
        var foreign = orderedPlan is { IsOverlay: true } && orderedId != managed?.OverlayPlanId;
        var inHorizon = date < context.Today.AddDays(Horizon);
        // 管理员给调休上学日预定了课表时以管理员为准，不再按调休另建临时层。
        var adminOrdered = orderedPlan is { IsOverlay: false } || managed?.DisplacedOrderedPlanId is not null;
        var needed = force || serverItems.Count > 0 || locals.Count > 0 || makeup is not null && !adminOrdered ||
                     foreign && State.ServerManaged && inHorizon;
        if (!needed)
        {
            if (managed is not null)
                Release(date, key, managed, ResolveBase(date, managed.MakeupWeekday, managed, orderedId, orderedPlan, full).Plan, context);
            return;
        }

        var (basePlan, basePlanId) = ResolveBase(date, makeup, managed, orderedId, orderedPlan, full);
        if (basePlan is null || basePlanId is null)
        {
            Conflict(serverItems, ScheduleAdjustmentConflictCodes.NoPlan);
            if (managed is not null) Release(date, key, managed, null, context);
            return;
        }
        var baseSubjects = basePlan.Classes.Select(x => x.SubjectId).ToList();

        ClassPlan? overlay = null;
        if (managed is not null && orderedId == managed.OverlayPlanId && orderedPlan is { IsOverlay: true })
        {
            overlay = orderedPlan;
            DetectEdits(key, managed, overlay, baseSubjects, serverItems, locals, context);
            // 基础课表或其时间表换了：插件建立的临时层按新的基础课表重建（刚从旧版导入的记录基线未知，不比较）。
            var stale = overlay.Classes.Count != baseSubjects.Count ||
                        full && managed.Created &&
                        (managed.BasePlanId != Guid.Empty && managed.BasePlanId != basePlanId ||
                         managed.LayoutSignature is not null && managed.LayoutSignature != host.GetLayoutSignature(basePlan));
            if (stale && !managed.Created)
            {
                // 接管的外来临时层与新的基础课表节数不同，无法再按节次对应：放弃托管。
                Unmanage(key, managed, serverItems, context);
                return;
            }
            if (stale)
            {
                host.SetOrderedPlan(date, null);
                host.DeleteOverlayPlan(managed.OverlayPlanId);
                context.HostChanged = true;
                overlay = null;
                managed = Create(date, key, basePlan, basePlanId.Value, makeup, managed.DisplacedOrderedPlanId, context);
                if (managed is null) { Conflict(serverItems, ScheduleAdjustmentConflictCodes.NoPlan); return; }
                overlay = host.ClassPlans[managed.OverlayPlanId];
            }
        }
        else if (foreign)
        {
            if (managed is { Created: true })
            {
                host.DeleteOverlayPlan(managed.OverlayPlanId);
                context.HostChanged = true;
            }
            if (orderedPlan!.Classes.Count != baseSubjects.Count)
            {
                Unmanage(key, managed, serverItems, context);
                return;
            }
            managed = new ManagedOverlay
            {
                OverlayPlanId = orderedId!.Value,
                BasePlanId = basePlanId.Value,
                MakeupWeekday = makeup,
                DisplacedOrderedPlanId = managed?.DisplacedOrderedPlanId,
                Created = false,
            };
            State.Dates[key] = managed;
            context.StateChanged = true;
            overlay = orderedPlan;
            DetectEdits(key, managed, overlay, baseSubjects, serverItems, locals, context);
            logger.LogInformation("已接管 {Date} 的临时层", key);
        }
        else
        {
            if (managed is { Created: true } && host.ClassPlans.ContainsKey(managed.OverlayPlanId))
            {
                host.DeleteOverlayPlan(managed.OverlayPlanId);
                context.HostChanged = true;
            }
            var displaced = orderedPlan is { IsOverlay: false } ? orderedId : managed?.DisplacedOrderedPlanId;
            managed = Create(date, key, basePlan, basePlanId.Value, makeup, displaced, context);
            if (managed is null)
            {
                Conflict(serverItems, ScheduleAdjustmentConflictCodes.NoPlan);
                return;
            }
            overlay = host.ClassPlans[managed.OverlayPlanId];
        }

        var result = ScheduleAdjustmentMath.Compose(baseSubjects, serverItems, locals, host.SubjectExists);
        foreach (var item in result.Results) _results[item.Id] = item;
        for (var index = 0; index < overlay.Classes.Count && index < result.Subjects.Count; index++)
        {
            var lesson = overlay.Classes[index];
            var subject = result.Subjects[index];
            if (lesson.SubjectId != subject)
            {
                lesson.SubjectId = subject;
                context.HostChanged = true;
            }
            var changed = subject != baseSubjects[index];
            if (lesson.IsChangedClass != changed)
            {
                lesson.IsChangedClass = changed;
                context.HostChanged = true;
            }
        }
        if (managed.Expected is null || !managed.Expected.SequenceEqual(result.Subjects) || managed.BasePlanId != basePlanId ||
            managed.MakeupWeekday != makeup || managed.Created && managed.LayoutSignature is null)
        {
            managed.Expected = [.. result.Subjects];
            managed.BasePlanId = basePlanId.Value;
            managed.MakeupWeekday = makeup;
            if (managed.Created) managed.LayoutSignature ??= host.GetLayoutSignature(basePlan);
            context.StateChanged = true;
        }
        _composed[key] = (baseSubjects, result.AppliedIds);
    }

    /// <summary>
    /// 托管临时层被人在 ClassIsland 里改过：与上次写入的科目不同的节次转为本地调课（保留老师的修改）。
    /// 基线未知（刚接管或升级导入）时，只把既不同于基础课表、也不同于服务端调课结果的节次视为本地修改。
    /// </summary>
    private void DetectEdits(string key, ManagedOverlay managed, ClassPlan overlay, List<Guid> baseSubjects,
        List<ScheduleSlotChange> serverItems, List<LocalScheduleAdjustment> locals, RunContext context)
    {
        var actual = overlay.Classes.Select(x => x.SubjectId).ToList();
        if (actual.Count != baseSubjects.Count) return;
        List<int> edited;
        if (managed.Expected is { } expected)
        {
            if (expected.Count != actual.Count) return;
            edited = Enumerable.Range(0, actual.Count).Where(i => actual[i] != expected[i]).ToList();
        }
        else
        {
            var composed = ScheduleAdjustmentMath.Compose(baseSubjects, serverItems, locals, host.SubjectExists).Subjects;
            edited = Enumerable.Range(0, actual.Count).Where(i => actual[i] != baseSubjects[i] && actual[i] != composed[i]).ToList();
        }
        foreach (var index in edited)
        {
            var local = new LocalScheduleAdjustment
            {
                Id = Guid.NewGuid(),
                Date = key,
                Index = index,
                FromSubjectId = baseSubjects[index],
                ToSubjectId = actual[index],
                Origin = ScheduleAdjustmentOrigins.ClassIsland,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            State.PendingLocal.Add(local);
            locals.Add(local);
            context.StateChanged = true;
        }
        if (edited.Count > 0) logger.LogInformation("{Date} 的临时层在 ClassIsland 中被修改，已记录 {Count} 节调课", key, edited.Count);
    }

    private ManagedOverlay? Create(DateTime date, string key, ClassPlan basePlan, Guid basePlanId, int? makeup, Guid? displaced, RunContext context)
    {
        if (host.CreateTempClassPlan(basePlanId, date) is not { } overlayId || !host.ClassPlans.ContainsKey(overlayId))
        {
            logger.LogWarning("{Date} 无法建立临时层", key);
            return null;
        }
        var managed = new ManagedOverlay
        {
            OverlayPlanId = overlayId,
            BasePlanId = basePlanId,
            LayoutSignature = host.GetLayoutSignature(basePlan),
            MakeupWeekday = makeup,
            DisplacedOrderedPlanId = displaced,
            Created = true,
            Expected = basePlan.Classes.Select(x => x.SubjectId).ToList(),
        };
        State.Dates[key] = managed;
        context.HostChanged = true;
        context.StateChanged = true;
        return managed;
    }

    /// <summary>
    /// 该日不再需要托管：插件建立的临时层删除并恢复被顶替的预定课表；接管的外来临时层改回基础科目后不再托管。
    /// </summary>
    private void Release(DateTime date, string key, ManagedOverlay managed, ClassPlan? basePlan, RunContext context)
    {
        var orderedId = host.GetOrderedPlanId(date);
        if (managed.Created)
        {
            if (orderedId == managed.OverlayPlanId)
            {
                var restore = managed.DisplacedOrderedPlanId is { } displaced && host.ClassPlans.ContainsKey(displaced) ? displaced : (Guid?)null;
                host.SetOrderedPlan(date, restore);
            }
            host.DeleteOverlayPlan(managed.OverlayPlanId);
            context.HostChanged = true;
        }
        else if (orderedId == managed.OverlayPlanId && basePlan is not null &&
                 host.ClassPlans.TryGetValue(managed.OverlayPlanId, out var overlay) && overlay.Classes.Count == basePlan.Classes.Count)
        {
            for (var index = 0; index < overlay.Classes.Count; index++)
            {
                var subject = basePlan.Classes[index].SubjectId;
                if (overlay.Classes[index].SubjectId == subject && !overlay.Classes[index].IsChangedClass) continue;
                overlay.Classes[index].SubjectId = subject;
                overlay.Classes[index].IsChangedClass = false;
                context.HostChanged = true;
            }
        }
        State.Dates.Remove(key);
        context.StateChanged = true;
    }

    private void Unmanage(string key, ManagedOverlay? managed, List<ScheduleSlotChange> serverItems, RunContext context)
    {
        _unmanaged.Add(key);
        Conflict(serverItems, ScheduleAdjustmentConflictCodes.ForeignOverlay);
        if (managed is not null && State.Dates.Remove(key)) context.StateChanged = true;
    }

    /// <summary>
    /// 基础课表：管理员给该日预定的普通课表优先；调休上学日取所跟星期那天的课表；否则为宿主按规则匹配的课表。
    /// 一律忽略临时层（包括自己的），避免把某天的换课复制进另一天。定时核对沿用记录的基础课表，不重新解析。
    /// </summary>
    private (ClassPlan? Plan, Guid? Id) ResolveBase(DateTime date, int? makeup, ManagedOverlay? managed, Guid? orderedId,
        ClassPlan? orderedPlan, bool full)
    {
        if (!full && managed is not null && managed.MakeupWeekday == makeup && managed.BasePlanId != Guid.Empty &&
            host.ClassPlans.TryGetValue(managed.BasePlanId, out var known))
            return (known, managed.BasePlanId);
        if (orderedPlan is { IsOverlay: false }) return (orderedPlan, orderedId);
        if (managed?.DisplacedOrderedPlanId is { } displaced && host.ClassPlans.TryGetValue(displaced, out var plan)) return (plan, displaced);
        if (makeup is { } weekday) return PlanIgnoringOverlays(HolidayScheduleApplier.SourceDate(date, weekday));
        return PlanIgnoringOverlays(date);
    }

    private (ClassPlan? Plan, Guid? Id) PlanIgnoringOverlays(DateTime date)
    {
        if (host.GetOrderedPlanId(date) is { } orderedId && host.ClassPlans.TryGetValue(orderedId, out var ordered))
        {
            if (!ordered.IsOverlay) return (ordered, orderedId);
            var rule = host.ResolveRulePlan(date, out var ruleId);
            return rule is { IsOverlay: false } ? (rule, ruleId) : (null, null);
        }
        var plan = host.GetClassPlan(date, out var planId);
        return plan is { IsOverlay: false } ? (plan, planId) : (null, null);
    }

    private void Conflict(IEnumerable<ScheduleSlotChange> items, string code)
    {
        foreach (var item in items) _results[item.Id] = ScheduleAdjustmentMath.Conflict(item.Id, code);
    }

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>从信封中取出服务端调课集合；单独成类以便不经网络直接测试。</summary>
public static class ScheduleAdjustmentsMessage
{
    public static bool TryRead(Envelope envelope, out ScheduleAdjustmentSet set)
    {
        set = null!;
        if (envelope.Type != Protocol.MessageTypeScheduleAdjustments || envelope.Payload is null) return false;
        try
        {
            set = JsonSerializer.Deserialize<ScheduleAdjustmentSet>(JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return set is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
