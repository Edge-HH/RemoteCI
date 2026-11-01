using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>唤醒自动化后台服务：课表事件或设备上线产生了立即到期的任务时，不必等下一个轮询周期。</summary>
public sealed class AutomationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>自动化规则的增删改查；v1 只允许系统管理员管理。</summary>
public sealed class AutomationRuleService(AppDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AutomationRuleDto>> ListAsync(CancellationToken ct = default) =>
        (await db.AutomationRules.AsNoTracking().OrderBy(x => x.CreatedAtUnixMs).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<AutomationRuleDto> GetAsync(Guid id, CancellationToken ct = default) =>
        ToDto(await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("自动化规则不存在。"));

    public async Task<AutomationRuleDto> SaveAsync(AppUser actor, AutomationRuleDto input, CancellationToken ct = default)
    {
        RequireAdmin(actor);
        AutomationRules.Validate(input);
        var classIds = input.Target.ClassIds.Distinct().ToList();
        var groupIds = input.Target.GroupIds.Distinct().ToList();
        if (await db.Classrooms.CountAsync(x => classIds.Contains(x.Id), ct) != classIds.Count ||
            await db.ClassGroups.CountAsync(x => groupIds.Contains(x.Id), ct) != groupIds.Count)
            throw new ArgumentException("所选班级或分组已不存在，请刷新后重试。");
        input.Target = new AutomationTarget { ClassIds = classIds, GroupIds = groupIds };

        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        AutomationRule row;
        if (input.Id is { } id)
        {
            row = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("自动化规则不存在。");
        }
        else
        {
            row = new AutomationRule
            {
                Id = Guid.NewGuid(),
                CreatedByUserId = actor.Id,
                CreatedByName = actor.DisplayName,
                CreatedAtUnixMs = now,
            };
            db.AutomationRules.Add(row);
        }
        row.Name = input.Name;
        row.Enabled = input.Enabled;
        row.TriggerJson = JsonSerializer.Serialize(input.Trigger, JsonDefaults.Options);
        row.ActionsJson = JsonSerializer.Serialize(input.Actions, JsonDefaults.Options);
        row.TargetJson = JsonSerializer.Serialize(input.Target, JsonDefaults.Options);
        row.ValidFrom = input.ValidFrom;
        row.ValidTo = input.ValidTo;
        // 修改规则即由修改人承担执行身份，避免停用的原作者让规则永远失败。
        row.CreatedByUserId = actor.Id;
        row.CreatedByName = actor.DisplayName;
        row.UpdatedAtUnixMs = now;
        if (!row.Enabled)
        {
            // 停用时取消尚未执行的延迟任务。
            await db.AutomationRuns.Where(x => x.RuleId == row.Id && x.Status == AutomationRunStatus.Pending)
                .ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task SetEnabledAsync(AppUser actor, Guid id, bool enabled, CancellationToken ct = default)
    {
        var dto = await GetAsync(id, ct);
        dto.Enabled = enabled;
        await SaveAsync(actor, dto, ct);
    }

    public async Task DeleteAsync(AppUser actor, Guid id, CancellationToken ct = default)
    {
        RequireAdmin(actor);
        var row = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("自动化规则不存在。");
        db.AutomationRules.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationRunView>> ListRunsAsync(Guid? ruleId, int limit, CancellationToken ct = default)
    {
        var query = db.AutomationRuns.AsNoTracking();
        if (ruleId is { } id) query = query.Where(x => x.RuleId == id);
        return (await query.OrderByDescending(x => x.DueAtUnixMs).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct))
            .Select(AutomationRunView.From)
            .ToList();
    }

    /// <summary>需要“自动化”系统权限；调用方须已加载 actor 的角色。</summary>
    internal static void RequireAdmin(AppUser actor)
    {
        if (!AccountPermissions.HasManagement(actor, ManagementPermissions.ManageAutomation))
            throw new UnauthorizedAccessException("没有管理自动化规则的权限。");
    }

    public static AutomationRuleDto ToDto(AutomationRule row)
    {
        var dto = new AutomationRuleDto
        {
            Id = row.Id,
            Name = row.Name,
            Enabled = row.Enabled,
            Trigger = JsonSerializer.Deserialize<AutomationTrigger>(row.TriggerJson, JsonDefaults.Options) ?? new(),
            Actions = JsonSerializer.Deserialize<List<AutomationAction>>(row.ActionsJson, JsonDefaults.Options) ?? [],
            Target = JsonSerializer.Deserialize<AutomationTarget>(row.TargetJson, JsonDefaults.Options) ?? new(),
            ValidFrom = row.ValidFrom,
            ValidTo = row.ValidTo,
            CreatedByName = row.CreatedByName,
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUnixMs),
            LastRunAt = row.LastRunAtUnixMs is { } last ? DateTimeOffset.FromUnixTimeMilliseconds(last) : null,
            LastSuccess = row.LastSuccess,
            LastResult = row.LastResult,
        };
        dto.Description = AutomationRules.Describe(dto);
        return dto;
    }
}

/// <summary>
/// 自动化执行引擎：把触发转成按班级去重的执行记录（AutomationRun），到期后以规则修改人的身份逐个动作下发。
/// 定时触发按每个目标班级的教室端本地时间计算；错过触发时刻 5 分钟以上不再补跑，记为“已错过”。
/// </summary>
public sealed class AutomationEngine(
    AppDbContext db,
    ClassroomService classrooms,
    IStateStore state,
    HolidayCalendarService holidays,
    PeerRegistry peers,
    IdentityCoordinator identities,
    ProfileDispatchService profiles,
    UserNotificationService notifications,
    AutomationSignal signal,
    ILogger<AutomationEngine> logger)
{
    public static readonly TimeSpan MissWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScheduleLookBack = TimeSpan.FromHours(1);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private HolidayCalendar? _calendar;

    /// <summary>
    /// 为已到触发时刻的定时规则生成执行记录（每班每天每个时刻至多一条）。只回看 1 小时：
    /// 更早的触发不再登记，避免每个轮询周期都为当天已过去的时刻逐条查库。
    /// </summary>
    public async Task EnqueueScheduledAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var candidates = new List<(AutomationRule Rule, Guid ClassId, string ClassName, string Key, string Trigger, DateTimeOffset Due)>();
        foreach (var (rule, dto) in await LoadEnabledAsync(AutomationTriggerKind.Schedule, ct))
        {
            if (!AutomationRules.TryParseTime(dto.Trigger.Time, out var time)) continue;
            foreach (var (classId, className) in await ResolveTargetsAsync(dto.Target, ct))
            {
                var offset = ClassClock.Offset(state, classId, now);
                var today = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
                var due = new DateTimeOffset(today.ToDateTime(time), offset);
                if (due > now || now - due > ScheduleLookBack) continue;
                if (!dto.Trigger.Weekdays.Contains(IsoWeekday(today)) || !InValidRange(dto, today)) continue;
                if (dto.Trigger.SchoolDaysOnly && !await IsSchoolDayAsync(classId, today, ct)) continue;
                candidates.Add((rule, classId, className,
                    $"{rule.Id:N}:{classId:N}:{ClassSchedulePeriods.DateKey(today)}T{dto.Trigger.Time}",
                    AutomationRules.DescribeTrigger(dto.Trigger), due));
            }
        }
        if (candidates.Count == 0) return;
        var keys = candidates.Select(x => x.Key).ToList();
        var existing = (await db.AutomationRuns.AsNoTracking().Where(x => keys.Contains(x.DedupeKey))
            .Select(x => x.DedupeKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(x => !existing.Contains(x.Key)))
        {
            var status = now - candidate.Due > MissWindow ? AutomationRunStatus.Missed : AutomationRunStatus.Pending;
            await AddRunAsync(candidate.Rule, candidate.ClassId, candidate.ClassName, candidate.Key, candidate.Trigger, candidate.Due,
                status, status == AutomationRunStatus.Missed ? "服务端在触发时刻未运行，已跳过" : null, ct);
        }
    }

    public async Task OnClassEventAsync(Guid classId, ClassEvent value, DateTimeOffset now, CancellationToken ct = default)
    {
        if (value.Event is not (ClassEventKind.OnClass or ClassEventKind.OnBreaking or ClassEventKind.OnAfterSchool)) return;
        var added = false;
        foreach (var (rule, dto) in await LoadEnabledAsync(AutomationTriggerKind.ClassEvent, ct))
        {
            if (dto.Trigger.Event != value.Event) continue;
            if (dto.Trigger.Subject is { } subject && !string.Equals(subject, value.Subject?.Trim(), StringComparison.Ordinal)) continue;
            if (!InValidRange(dto, ClassClock.Today(state, classId, now))) continue;
            if ((await ResolveTargetsAsync(dto.Target, ct)).FirstOrDefault(x => x.ClassId == classId) is not { ClassName: { } className }) continue;
            var due = now.AddMinutes(dto.Trigger.DelayMinutes);
            added |= await AddRunAsync(rule, classId, className, $"{rule.Id:N}:{classId:N}:event:{value.Id}",
                AutomationRules.DescribeTrigger(dto.Trigger), due, AutomationRunStatus.Pending, null, ct);
        }
        if (added) signal.Notify();
    }

    public async Task OnDeviceOnlineAsync(Guid classId, DateTimeOffset at, CancellationToken ct = default)
    {
        var added = false;
        foreach (var (rule, dto) in await LoadEnabledAsync(AutomationTriggerKind.DeviceOnline, ct))
        {
            if (!InValidRange(dto, ClassClock.Today(state, classId, at))) continue;
            if ((await ResolveTargetsAsync(dto.Target, ct)).FirstOrDefault(x => x.ClassId == classId) is not { ClassName: { } className }) continue;
            added |= await AddRunAsync(rule, classId, className, $"{rule.Id:N}:{classId:N}:online:{at.ToUnixTimeMilliseconds()}",
                AutomationRules.DescribeTrigger(dto.Trigger), at.AddSeconds(dto.Trigger.DelaySeconds), AutomationRunStatus.Pending, null, ct);
        }
        if (added) signal.Notify();
    }

    /// <summary>手动“立即运行”：对全部目标班级执行一次，忽略触发条件与生效日期。</summary>
    public async Task<IReadOnlyList<AutomationRunView>> RunNowAsync(AppUser actor, Guid ruleId, DateTimeOffset now, CancellationToken ct = default)
    {
        AutomationRuleService.RequireAdmin(actor);
        var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == ruleId, ct)
            ?? throw new KeyNotFoundException("自动化规则不存在。");
        var dto = AutomationRuleService.ToDto(rule);
        var runs = new List<AutomationRun>();
        foreach (var (classId, className) in await ResolveTargetsAsync(dto.Target, ct))
        {
            // 直接登记为“执行中”：若记为待执行，逐班执行期间后台服务会把后面的班级再执行一遍。
            var run = NewRun(rule, classId, className, $"manual:{Guid.NewGuid():N}", $"{actor.DisplayName} 手动运行", now, AutomationRunStatus.Running);
            run.StartedAtUnixMs = now.ToUnixTimeMilliseconds();
            db.AutomationRuns.Add(run);
            runs.Add(run);
        }
        await db.SaveChangesAsync(ct);
        foreach (var run in runs) await ExecuteAsync(run, rule, dto, now, ct);
        return runs.Select(AutomationRunView.From).ToList();
    }

    /// <summary>执行到期的任务；积压超过 30 分钟的延迟任务视为错过（例如“放学后关机”不应在第二天早上执行）。</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        // 服务端在执行途中停止会留下“执行中”的记录，超过 10 分钟视为中断。
        var stuckBefore = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        await db.AutomationRuns
            .Where(x => x.Status == AutomationRunStatus.Running && x.StartedAtUnixMs < stuckBefore)
            .ExecuteUpdateAsync(x => x
                .SetProperty(y => y.Status, AutomationRunStatus.Failed)
                .SetProperty(y => y.Message, "执行中断（服务端重启）")
                .SetProperty(y => y.FinishedAtUnixMs, nowMs), ct);
        var due = await db.AutomationRuns
            .Where(x => x.Status == AutomationRunStatus.Pending && x.DueAtUnixMs <= nowMs)
            .OrderBy(x => x.DueAtUnixMs)
            .Take(50)
            .ToListAsync(ct);
        foreach (var run in due)
        {
            var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == run.RuleId, ct);
            if (rule is null || !rule.Enabled)
            {
                Finish(run, AutomationRunStatus.Missed, "规则已停用或删除", now);
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (now - DateTimeOffset.FromUnixTimeMilliseconds(run.DueAtUnixMs) > TimeSpan.FromMinutes(30))
            {
                Finish(run, AutomationRunStatus.Missed, "到期时服务端未运行，已跳过", now);
                await db.SaveChangesAsync(ct);
                continue;
            }
            await ExecuteAsync(run, rule, AutomationRuleService.ToDto(rule), now, ct);
        }
        return due.Count;
    }

    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now.AddDays(-90).ToUnixTimeMilliseconds();
        await db.AutomationRuns.Where(x => x.DueAtUnixMs < cutoff).ExecuteDeleteAsync(ct);
    }

    private async Task ExecuteAsync(AutomationRun run, AutomationRule rule, AutomationRuleDto dto, DateTimeOffset now, CancellationToken ct)
    {
        run.Status = AutomationRunStatus.Running;
        run.StartedAtUnixMs = now.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);

        var results = new List<string>();
        var success = true;
        try
        {
            var actor = await db.Users.AsNoTracking().Include(x => x.RoleDefinition)
                .SingleOrDefaultAsync(x => x.Id == rule.CreatedByUserId, ct);
            if (actor is null || !actor.Enabled || !AccountPermissions.HasManagement(actor, ManagementPermissions.ManageAutomation))
                throw new InvalidOperationException("规则最后修改人已停用或不再拥有自动化权限，请由有自动化权限的账号重新保存规则。");
            var profile = await identities.GetProfileAsync(actor.Id, ct)
                ?? throw new InvalidOperationException("无法读取规则修改人的账号信息。");
            var forceSender = await identities.GetForceSenderInTitleAsync(ct);
            using var auditScope = CommandAuditContext.Begin(CommandAuditSource.Automation, rule.Name);
            foreach (var action in dto.Actions)
            {
                var (ok, message) = await RunActionAsync(action, run.ClassId, actor, profile, forceSender, ct);
                success &= ok;
                results.Add($"{AutomationRules.DescribeAction(action)}：{message}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 任何异常都要落成“失败”，不能让记录停在“执行中”。
            success = false;
            var expected = ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException
                or KeyNotFoundException or ProfileRevisionException;
            if (!expected) logger.LogWarning(ex, "自动化规则 {Rule} 执行出错", rule.Name);
            results.Add(expected ? ex.Message : "执行出错，请查看服务端日志");
        }

        var summary = CommandAuditLog.Truncate(string.Join("；", results), 1000);
        Finish(run, success ? AutomationRunStatus.Succeeded : AutomationRunStatus.Failed, summary, DateTimeOffset.UtcNow);
        rule.LastRunAtUnixMs = run.FinishedAtUnixMs;
        rule.LastSuccess = success;
        rule.LastResult = CommandAuditLog.Truncate($"{run.ClassName}：{summary}", 200);
        await db.SaveChangesAsync(ct);
        if (!success)
        {
            try
            {
                await notifications.PublishAsync([rule.CreatedByUserId], UserNotificationKinds.AutomationFailed,
                    $"自动化「{rule.Name}」执行失败", $"{run.ClassName}：{summary}", null, ct,
                    expiresAt: DateTimeOffset.UtcNow.AddHours(12), classId: run.ClassId, link: "/Automation");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "发送自动化失败通知出错：{Rule}", rule.Name);
            }
        }
    }

    private async Task<(bool Success, string Message)> RunActionAsync(
        AutomationAction action, Guid classId, AppUser actor, UserProfile profile, bool forceSender, CancellationToken ct)
    {
        if (action.Kind == AutomationActionKind.TempLayers)
        {
            var stored = await db.StoredProfiles.AsNoTracking().Where(x => x.ClassId == classId)
                .Select(x => new { x.Id, x.Revision }).SingleOrDefaultAsync(ct);
            if (stored is null) return (false, "该班没有服务端档案");
            var results = await profiles.ApplyAsync(actor, new ProfileDispatchRequest
            {
                Items = [new ProfileIdRequest { Id = stored.Id, Revision = stored.Revision }],
                Mode = ProfileApplyMode.TempLayers,
                ReplaceExistingTempLayers = action.ReplaceExistingTempLayers,
                ClassIds = [classId],
            }, classId, ct);
            return results.All(x => x.Success)
                ? (true, results.FirstOrDefault()?.Message ?? "已下发")
                : (false, string.Join("；", results.Where(x => !x.Success).Select(x => x.Message)));
        }

        var command = new CommandMessage { ClassId = classId, RequestedBy = profile };
        switch (action.Kind)
        {
            case AutomationActionKind.Notification:
                command.Command = CommandKind.SendNotification;
                command.Notification = CloneNotification(action.Notification!, forceSender);
                break;
            case AutomationActionKind.ClearNotifications:
                command.Command = CommandKind.ClearNotifications;
                break;
            case AutomationActionKind.MainMenu:
                command.Command = CommandKind.SetMainMenuVisibility;
                command.MainMenuVisible = action.MainMenuVisible;
                break;
            case AutomationActionKind.Volume:
                command.Command = CommandKind.Volume;
                command.Volume = action.Volume;
                break;
            case AutomationActionKind.Power:
                command.Command = CommandKind.Power;
                command.PowerAction = action.PowerAction;
                break;
            case AutomationActionKind.Extension:
                command.Command = CommandKind.RunExtension;
                command.ExtensionId = action.ExtensionId;
                command.ExtensionArgs = action.ExtensionArgs ?? [];
                break;
            default:
                return (false, "不支持的动作");
        }
        // 规则以最后修改人的身份执行：每个动作仍需其拥有对应权限（按全局角色计算，与批量控制一致）。
        if (!profile.Permissions.HasFlag(CommandPermissions.Required(command.Command)))
            return (false, "规则修改人没有该动作的权限");
        var result = await peers.SendCommandAndWaitAsync(command, classId, CommandTimeout, ct);
        return (result.Success, string.IsNullOrWhiteSpace(result.Message) ? (result.Success ? "成功" : result.Code) : result.Message);
    }

    private static NotificationRequest CloneNotification(NotificationRequest source, bool forceSender) => new()
    {
        Title = source.Title,
        Message = source.Message,
        ForceSenderInTitle = forceSender,
        IsNotificationEffectEnabled = source.IsNotificationEffectEnabled,
        IsNotificationSoundEnabled = source.IsNotificationSoundEnabled,
        IsSpeechEnabled = source.IsSpeechEnabled,
        IsNotificationTopmostEnabled = source.IsNotificationTopmostEnabled,
        DurationSeconds = source.DurationSeconds,
        RepeatCounts = source.RepeatCounts,
        IsRollingEnabled = source.IsRollingEnabled,
    };

    private async Task<List<(AutomationRule Rule, AutomationRuleDto Dto)>> LoadEnabledAsync(
        AutomationTriggerKind kind, CancellationToken ct)
    {
        var rows = await db.AutomationRules.Where(x => x.Enabled).ToListAsync(ct);
        return rows.Select(x => (x, AutomationRuleService.ToDto(x))).Where(x => x.Item2.Trigger.Kind == kind).ToList();
    }

    private async Task<IReadOnlyList<(Guid ClassId, string ClassName)>> ResolveTargetsAsync(AutomationTarget target, CancellationToken ct)
    {
        var ids = await classrooms.ResolveTargetClassIdsAsync(target.ClassIds, target.GroupIds, ct);
        var names = await db.Classrooms.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return ids.Where(names.ContainsKey).Select(id => (id, names[id])).ToList();
    }

    /// <summary>上学日：调休上学日一定上学，放假日一定不上学，其余按周一至周五。</summary>
    private async Task<bool> IsSchoolDayAsync(Guid classId, DateOnly date, CancellationToken ct)
    {
        _calendar ??= await holidays.BuildCalendarAsync(ct);
        var key = ClassSchedulePeriods.DateKey(date);
        var day = _calendar.Enabled ? _calendar.Days.FirstOrDefault(x => x.Date == key) : null;
        if (day?.Kind == HolidayDayKinds.Makeup) return true;
        if (day?.Kind == HolidayDayKinds.Off) return false;
        return IsoWeekday(date) <= 5;
    }

    private async Task<bool> AddRunAsync(
        AutomationRule rule, Guid classId, string className, string key, string trigger, DateTimeOffset due,
        AutomationRunStatus status, string? message, CancellationToken ct)
    {
        if (await db.AutomationRuns.AnyAsync(x => x.DedupeKey == key, ct)) return false;
        var run = NewRun(rule, classId, className, key, trigger, due, status);
        if (status == AutomationRunStatus.Missed) Finish(run, status, message, due);
        db.AutomationRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
            return status == AutomationRunStatus.Pending;
        }
        catch (DbUpdateException)
        {
            // 唯一索引冲突：同一触发已由并发流程登记。
            db.Entry(run).State = EntityState.Detached;
            return false;
        }
    }

    private static AutomationRun NewRun(
        AutomationRule rule, Guid classId, string className, string key, string trigger, DateTimeOffset due, AutomationRunStatus status) => new()
    {
        Id = Guid.NewGuid(),
        RuleId = rule.Id,
        RuleName = rule.Name,
        ClassId = classId,
        ClassName = className,
        DedupeKey = key,
        TriggerText = CommandAuditLog.Truncate(trigger, 100),
        DueAtUnixMs = due.ToUnixTimeMilliseconds(),
        Status = status,
    };

    private static void Finish(AutomationRun run, AutomationRunStatus status, string? message, DateTimeOffset at)
    {
        run.Status = status;
        run.Message = message is null ? null : CommandAuditLog.Truncate(message, 1000);
        run.FinishedAtUnixMs = at.ToUnixTimeMilliseconds();
    }

    private bool InValidRange(AutomationRuleDto rule, DateOnly date)
    {
        var key = ClassSchedulePeriods.DateKey(date);
        return (rule.ValidFrom is null || string.CompareOrdinal(key, rule.ValidFrom) >= 0) &&
            (rule.ValidTo is null || string.CompareOrdinal(key, rule.ValidTo) <= 0);
    }

    public static int IsoWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
}

/// <summary>
/// 自动化后台服务：每 15 秒检查定时触发并执行到期任务；课表事件与设备上线会立即唤醒。
/// 设备上线事件来自 <see cref="PeerRegistry.PluginPresenceChanged"/>。
/// </summary>
public sealed class AutomationWorker(
    IServiceScopeFactory scopeFactory,
    PeerRegistry peers,
    AutomationSignal signal,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly Channel<PluginPresenceChange> _online = Channel.CreateBounded<PluginPresenceChange>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.OperationsWorkers) peers.PluginPresenceChanged += OnPresence;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        peers.PluginPresenceChanged -= OnPresence;
        return base.StopAsync(cancellationToken);
    }

    private void OnPresence(PluginPresenceChange change)
    {
        if (!change.Online || change.ClassId is null) return;
        _online.Writer.TryWrite(change);
        signal.Notify();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OperationsWorkers) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<AutomationEngine>();
                while (_online.Reader.TryRead(out var change))
                    await engine.OnDeviceOnlineAsync(change.ClassId!.Value, change.At, stoppingToken);
                var now = timeProvider.GetUtcNow();
                await engine.EnqueueScheduledAsync(now, stoppingToken);
                await engine.RunDueAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (now >= _nextPrune)
                {
                    _nextPrune = now.AddHours(12);
                    await engine.PruneAsync(now, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "执行自动化规则失败");
            }
            await signal.WaitAsync(Interval, stoppingToken);
        }
    }
}
