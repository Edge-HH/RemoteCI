using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>换课申请操作失败；Status 为对应的 HTTP 状态码，WebUI 直接展示 Message。</summary>
public sealed class SwapOperationException(string code, string message, int status = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    /// <summary>需要确认的冲突（Code 为 SWAP_CONFLICTS 时）。</summary>
    public IReadOnlyList<SwapConflict>? Conflicts { get; init; }
}

/// <summary>
/// 老师换课申请：老师发起（互换两节课或把目标课替换为自己任教的学科），对方老师审批后
/// 写成服务端调课记录（<see cref="ScheduleAdjustmentService"/>），由教室端插件生成临时层生效；
/// 开启强制换课权限的老师可直接生效，对方老师可撤回，班主任与系统管理员也可撤回已生效的换课。
/// 执行前总是按服务端叠加调课后的最新课表复核课位，避免把过期的选择写进教室端。
/// 永久换课仍由班主任在课表页操作，这里只产生临时（按日期）的改动。
/// </summary>
public sealed class ScheduleSwapService(
    AppDbContext db,
    IStateStore state,
    PeerRegistry peers,
    ScheduleAdjustmentService adjustments,
    ScheduleConflictService conflicts,
    IScheduleAdjustmentChannel channel,
    UserNotificationService notifications,
    AuthorizationSyncService authorizationSync,
    ILogger<ScheduleSwapService> logger)
{
    public const int MaxReasonLength = 200;
    public const string ForceWarning = "仅在需要紧急换课时使用，请提前与对方沟通并达成一致。";
    private const string SiblingInvalidReason = "同一节课已有其他换课生效";

    /// <summary>同一时间只执行一个换课：复核课位、写入调课与等待回执之间不能插入其他换课。</summary>
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);

    /// <summary>遗留格式：旧版逐节命令下发的单节改动，只用于升级回填（见 ScheduleAdjustmentService.BackfillAsync）。</summary>
    public sealed record AppliedChange(
        Guid ClassId,
        string Date,
        int Index,
        Guid FromSubjectId,
        string FromSubject,
        string ToSubject,
        string? PreviousOverrideTeacher,
        string? PreviousOverrideSubject,
        string? NewOverrideTeacher);

    private sealed record ResolvedSlot(
        Classroom Classroom, ScheduleBundle Bundle, ScheduleDay Day, CourseEntry Course)
    {
        public Guid ClassId => Classroom.Id;
        public bool SameSlot(ResolvedSlot other) =>
            ClassId == other.ClassId && Day.Date == other.Day.Date && Course.Index == other.Course.Index;
    }

    /// <summary>换课后的一节课：课位、要换入的科目与应上课老师（可能与科目默认老师不同）。</summary>
    private sealed record SlotChange(ResolvedSlot Slot, SubjectEntry To, string? Teacher);

    private sealed record Actor(AppUser User, UserPermissions Permissions)
    {
        public string Name => string.IsNullOrWhiteSpace(User.DisplayName) ? User.UserName ?? "" : User.DisplayName.Trim();
    }

    // ---------- 查询 ----------

    public async Task<SwapCatalog> GetCatalogAsync(Guid userId, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        RequirePermission(actor, UserPermissions.RequestScheduleSwap);
        var catalog = new SwapCatalog
        {
            TeacherName = actor.Name,
            CanForce = actor.Permissions.HasFlag(UserPermissions.ForceScheduleSwap),
        };
        var mySubjects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var classroom in (await db.Classrooms.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.CreatedAt))
        {
            if (state.GetLatestSchedule(classroom.Id) is not { } bundle) continue;
            var today = ClassToday(classroom.Id);
            var selectable = bundle.Days.Where(x => IsSelectableDate(x.Date, today))
                .OrderBy(x => x.Date, StringComparer.Ordinal).ToList();
            if (!selectable.Any(x => x.Enabled && x.Courses.Any(c => c.Enabled))) continue;
            var item = new SwapCatalogClass
            {
                ClassId = classroom.Id,
                ClassName = classroom.Name,
                Subjects = bundle.Subjects.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).ToList(),
                MinDate = FormatDate(today),
                MaxDate = selectable[^1].Date,
            };
            foreach (var subject in bundle.Subjects.Where(x => TeacherBindingService.Matches(actor.Name, x.Teacher)))
                mySubjects.Add(subject.Name);
            foreach (var day in selectable.Where(x => x.Enabled))
            {
                var catalogDay = CatalogDay(actor, day);
                if (catalogDay.Courses.Count == 0) continue;
                foreach (var course in catalogDay.Courses.Where(x => x.Mine && !string.IsNullOrWhiteSpace(x.Subject)))
                    mySubjects.Add(course.Subject);
                if (catalogDay.Courses.Any(x => x.Mine)) item.MyDates.Add(day.Date);
                // 旧版客户端只读 days：保留今天起 7 天，更远的日期由新版客户端按天读取。
                if (IsWithinDays(day.Date, today, ScheduleBundle.DisplayDays)) item.Days.Add(catalogDay);
            }
            catalog.Classes.Add(item);
        }
        catalog.MySubjects = [.. mySubjects];
        return catalog;
    }

    /// <summary>换课页选中某班某天后读取当天的课程；当天放假或没有课表时返回空课程列表。</summary>
    public async Task<SwapCatalogDay> GetCatalogDayAsync(Guid userId, Guid classId, string date, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        RequirePermission(actor, UserPermissions.RequestScheduleSwap);
        var classroom = await db.Classrooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == classId, ct)
            ?? throw Invalid("班级不存在");
        var bundle = state.GetLatestSchedule(classroom.Id)
            ?? throw Invalid($"{classroom.Name} 的课表尚未同步，请稍后再试");
        if (!IsSelectableDate(date, ClassToday(classroom.Id)))
            throw Invalid($"只能选择今天起 {ScheduleBundle.HorizonDays} 天内的日期");
        var day = bundle.Days.FirstOrDefault(x => x.Date == date)
            ?? throw Invalid(bundle.Days.Count == 0
                ? $"{classroom.Name} 的课表尚未同步，请稍后再试"
                : $"{classroom.Name} 教室端只同步到 {bundle.Days.Max(x => x.Date)} 的课表，更远的日期需要升级教室端插件");
        return day.Enabled ? CatalogDay(actor, day) : new SwapCatalogDay { Date = day.Date };
    }

    private static SwapCatalogDay CatalogDay(Actor actor, ScheduleDay day) => new()
    {
        Date = day.Date,
        Courses = day.Courses.Where(x => x.Enabled).OrderBy(x => x.Index).Select(course => new SwapCatalogCourse
        {
            Index = course.Index,
            Label = course.Label,
            Subject = course.Subject,
            Teacher = course.Teacher,
            StartTime = course.StartTime,
            EndTime = course.EndTime,
            Mine = TeacherBindingService.Matches(actor.Name, course.Teacher),
        }).ToList(),
    };

    /// <summary>box：incoming（待我处理/被强制换走的）、outgoing（我发起的）、all。</summary>
    public async Task<IReadOnlyList<SwapRequestView>> ListAsync(
        Guid userId, string? box, SwapRequestStatus? status, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        // SQLite 无法翻译 DateTimeOffset 排序；按用户相关过滤后在内存排序，数据量很小。
        var candidates = await db.ScheduleSwapRequests.AsNoTracking()
            .Where(x => status == null || x.Status == status)
            .ToListAsync(ct);
        var userKey = userId.ToString();
        var incoming = string.Equals(box, "incoming", StringComparison.OrdinalIgnoreCase);
        var outgoing = string.Equals(box, "outgoing", StringComparison.OrdinalIgnoreCase);
        var rows = candidates.Where(x =>
        {
            var mineOut = x.RequesterUserId == userId;
            var mineIn = x.ApproverUserIdsJson.Contains(userKey, StringComparison.OrdinalIgnoreCase);
            return incoming ? mineIn : outgoing ? mineOut : mineIn || mineOut;
        }).OrderByDescending(x => x.CreatedAt).Take(200).ToList();
        return await ToViewsAsync(rows, actor, ct);
    }

    public async Task<SwapRequestView> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await db.ScheduleSwapRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw NotFound();
        // 相关班级的班主任会收到换课通知，可以查看详情并撤回。
        if (row.RequesterUserId != userId && !Approvers(row).Contains(userId) && !await CanRevertAsync(actor, row, ct))
            throw NotFound();
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>
    /// 被强制换课的老师看过即视为已处理：把对应的强制换课通知标记已读，不再计入“待我处理”。
    /// ids 为空时标记该用户全部强制换课通知。
    /// </summary>
    public Task MarkForcedSeenAsync(Guid userId, IReadOnlyCollection<Guid>? ids, CancellationToken ct = default)
    {
        var query = db.UserNotifications.Where(x =>
            x.UserId == userId && x.Kind == UserNotificationKinds.SwapForced && x.ReadAt == null && x.SwapRequestId != null);
        if (ids is not null) query = query.Where(x => ids.Contains(x.SwapRequestId!.Value));
        var now = DateTimeOffset.UtcNow;
        return query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAt, now), ct);
    }

    /// <summary>按短编号（Id 前 8 位，聊天机器人输入用）查找与当前用户相关的申请。</summary>
    public async Task<Guid?> ResolveShortIdAsync(Guid userId, string shortId, CancellationToken ct = default)
    {
        var key = shortId.Trim().ToLowerInvariant();
        if (Guid.TryParse(key, out var full)) return full;
        if (key.Length < 4) return null;
        var mine = await ListAsync(userId, "all", null, ct);
        var matched = mine.Where(x => x.Id.ToString("N").StartsWith(key, StringComparison.Ordinal)).ToList();
        return matched.Count == 1 ? matched[0].Id : null;
    }

    /// <summary>“待我处理”数：待我审批的申请 + 还没看过的强制换课（与 SwapRequestView.NeedsAction 一致）。</summary>
    public Task<int> CountPendingForAsync(Guid userId, CancellationToken ct = default)
    {
        var userKey = userId.ToString();
        return db.ScheduleSwapRequests.CountAsync(x => x.ApproverUserIdsJson.Contains(userKey) &&
            (x.Status == SwapRequestStatus.Pending ||
             x.Status == SwapRequestStatus.Forced && db.UserNotifications.Any(n =>
                 n.UserId == userId && n.SwapRequestId == x.Id && n.Kind == UserNotificationKinds.SwapForced && n.ReadAt == null)), ct);
    }

    // ---------- 申请 ----------

    public async Task<SwapRequestView> CreateAsync(Guid userId, CreateSwapRequest request, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var (row, counterpart, approvers) = await BuildRequestAsync(actor, request, ct);

        if (request.Force && counterpart is not null && await IsForceLockedAsync(row, ct))
            throw new SwapOperationException(ApiErrorCodes.SwapForceLocked,
                "你当天对这节课的强制换课已被对方撤回，请提交普通换课申请", StatusCodes.Status409Conflict);
        if (await HasDuplicatePendingAsync(row, ct))
            throw new SwapOperationException(ApiErrorCodes.SwapStateConflict,
                "已有相同的待审批换课申请", StatusCodes.Status409Conflict);
        var found = await conflicts.CheckAsync(ToLessons(await PlanChangesAsync(row, actor, ct)), null, ct);
        RequireAcknowledged(found, request.AcknowledgeConflicts);
        row.AcknowledgedConflictsJson = SerializeAcknowledged(null, found);

        // 两节都是自己的课不需要对方审批；强制换课直接生效。
        if (counterpart is null || request.Force)
        {
            await ApplyAsync(row, actor, ct);
            row.Status = counterpart is null ? SwapRequestStatus.Approved : SwapRequestStatus.Forced;
            row.DecidedAt = DateTimeOffset.UtcNow;
            row.DecidedByUserId = counterpart is null ? userId : null;
            db.ScheduleSwapRequests.Add(row);
            await db.SaveChangesAsync(ct);
            if (row.Status == SwapRequestStatus.Forced)
            {
                await notifications.PublishAsync(approvers, UserNotificationKinds.SwapForced,
                    $"{actor.Name} 强制换走了你的课",
                    $"{Describe(row)}\n理由：{row.Reason}\n如未事先沟通，可在换课页撤回。", row.Id, ct);
            }
            await NotifyHomeroomAsync(row, actor.Name, ct);
            await InvalidateSiblingsAsync(row, ct);
        }
        else
        {
            row.Status = SwapRequestStatus.Pending;
            db.ScheduleSwapRequests.Add(row);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(approvers, UserNotificationKinds.SwapRequested,
                $"{actor.Name} 申请与你换课",
                $"{Describe(row)}\n理由：{row.Reason}", row.Id, ct);
        }
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>提交前预检冲突（不写入任何数据）：与 CreateAsync 相同的校验，返回检查出的冲突。</summary>
    public async Task<SwapPreviewResponse> PreviewAsync(Guid userId, CreateSwapRequest request, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var (row, _, _) = await BuildRequestAsync(actor, request, ct);
        return new SwapPreviewResponse
        {
            Conflicts = [.. await conflicts.CheckAsync(ToLessons(await PlanChangesAsync(row, actor, ct)), null, ct)],
        };
    }

    /// <summary>按申请内容解析课位并做基本校验，得到待保存的申请、对方课位（null 表示两节都是自己的课）与审批人。</summary>
    private async Task<(ScheduleSwapRequest Row, ResolvedSlot? Counterpart, List<Guid> Approvers)> BuildRequestAsync(
        Actor actor, CreateSwapRequest request, CancellationToken ct)
    {
        var userId = actor.User.Id;
        RequirePermission(actor, UserPermissions.RequestScheduleSwap);
        if (request.Force) RequirePermission(actor, UserPermissions.ForceScheduleSwap);
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw Invalid("请填写换课理由");
        if (reason.Length > MaxReasonLength) throw Invalid($"换课理由不能超过 {MaxReasonLength} 个字");
        if (request.Target is null) throw Invalid("请选择目标课");

        var target = await ResolveSlotAsync(request.Target, ct);
        var row = new ScheduleSwapRequest
        {
            Id = Guid.NewGuid(),
            Mode = request.Mode,
            Forced = request.Force,
            RequesterUserId = userId,
            Reason = reason,
            TargetClassId = target.ClassId,
            TargetDate = target.Day.Date,
            TargetIndex = target.Course.Index,
            TargetSubject = target.Course.Subject,
            TargetTeacher = target.Course.Teacher,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        ResolvedSlot? counterpart;
        if (request.Mode == SwapMode.Exchange)
        {
            if (request.Source is null) throw Invalid("请选择要换走的课");
            var source = await ResolveSlotAsync(request.Source, ct);
            if (source.SameSlot(target)) throw Invalid("要换走的课与目标课不能是同一节");
            if (string.Equals(source.Course.Subject, target.Course.Subject, StringComparison.Ordinal) &&
                string.Equals(source.Course.Teacher, target.Course.Teacher, StringComparison.Ordinal))
                throw Invalid("两节课的学科和老师相同，无需换课");
            var mineSource = TeacherBindingService.Matches(actor.Name, source.Course.Teacher);
            var mineTarget = TeacherBindingService.Matches(actor.Name, target.Course.Teacher);
            if (!mineSource && !mineTarget)
                throw new SwapOperationException(ApiErrorCodes.SwapNotOwn, "两节课中至少要有一节是你自己的课");
            if (source.ClassId != target.ClassId)
            {
                RequireSubject(target, source.Course.Subject);
                RequireSubject(source, target.Course.Subject);
            }
            row.SourceClassId = source.ClassId;
            row.SourceDate = source.Day.Date;
            row.SourceIndex = source.Course.Index;
            row.SourceSubject = source.Course.Subject;
            row.SourceTeacher = source.Course.Teacher;
            counterpart = mineSource ? (mineTarget ? null : target) : source;
        }
        else if (request.Mode == SwapMode.Replace)
        {
            var subject = request.SubjectName?.Trim();
            if (string.IsNullOrEmpty(subject)) throw Invalid("请选择要换入的学科");
            var catalog = await GetCatalogAsync(userId, ct);
            if (!catalog.MySubjects.Contains(subject, StringComparer.Ordinal))
                throw Invalid($"“{subject}”不是你任教的学科");
            RequireSubject(target, subject);
            var mineTarget = TeacherBindingService.Matches(actor.Name, target.Course.Teacher);
            if (mineTarget && string.Equals(target.Course.Subject, subject, StringComparison.Ordinal))
                throw Invalid("目标课已经是你的这门课，无需替换");
            row.ReplacementSubjectName = subject;
            counterpart = mineTarget ? null : target;
        }
        else
        {
            throw Invalid("未知的换课模式");
        }

        var lockSlot = counterpart ?? target;
        row.CounterpartClassId = lockSlot.ClassId;
        row.CounterpartDate = lockSlot.Day.Date;
        row.CounterpartIndex = lockSlot.Course.Index;
        var approvers = counterpart is null ? [] : await ResolveApproversAsync(counterpart, userId, ct);
        row.ApproverUserIdsJson = JsonSerializer.Serialize(approvers);
        return (row, counterpart, approvers);
    }

    public async Task<SwapRequestView> ApproveAsync(Guid userId, Guid id, string? note, bool acknowledgeConflicts = false,
        CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await LoadForDecisionAsync(id, userId, SwapRequestStatus.Pending, ct);
        if (!IsSelectableDate(row.TargetDate, ClassToday(row.TargetClassId)) ||
            (row.SourceDate is { } sourceDate && row.SourceClassId is { } sourceClass && !IsSelectableDate(sourceDate, ClassToday(sourceClass))))
        {
            row.Status = SwapRequestStatus.Expired;
            await db.SaveChangesAsync(ct);
            throw new SwapOperationException(ApiErrorCodes.SwapStateConflict, "课程日期已过，申请已失效", StatusCodes.Status409Conflict);
        }
        var found = await conflicts.CheckAsync(ToLessons(await PlanChangesAsync(row, actor, ct)), row.Id, ct);
        RequireAcknowledged(found, acknowledgeConflicts);
        row.AcknowledgedConflictsJson = SerializeAcknowledged(row.AcknowledgedConflictsJson, found);
        await ApplyAsync(row, actor, ct);
        row.Status = SwapRequestStatus.Approved;
        row.DecidedByUserId = userId;
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.DecisionNote = TrimNote(note);
        await db.SaveChangesAsync(ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        await notifications.PublishAsync([row.RequesterUserId], UserNotificationKinds.SwapApproved,
            $"{actor.Name} 同意了你的换课申请",
            $"{Describe(row)}{NoteSuffix(row.DecisionNote)}\n课表已临时调整。", row.Id, ct);
        await NotifyHomeroomAsync(row, actor.Name, ct);
        await InvalidateSiblingsAsync(row, ct);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    public async Task<SwapRequestView> RejectAsync(Guid userId, Guid id, string? note, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await LoadForDecisionAsync(id, userId, SwapRequestStatus.Pending, ct);
        row.Status = SwapRequestStatus.Rejected;
        row.DecidedByUserId = userId;
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.DecisionNote = TrimNote(note);
        await db.SaveChangesAsync(ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        await notifications.PublishAsync([row.RequesterUserId], UserNotificationKinds.SwapRejected,
            $"{actor.Name} 拒绝了你的换课申请",
            $"{Describe(row)}{NoteSuffix(row.DecisionNote)}", row.Id, ct);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    public async Task<SwapRequestView> CancelAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await db.ScheduleSwapRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        if (row.RequesterUserId != userId) throw NotFound();
        if (row.Status != SwapRequestStatus.Pending) throw StateConflict(row);
        row.Status = SwapRequestStatus.Cancelled;
        row.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        await notifications.PublishAsync(Approvers(row), UserNotificationKinds.SwapCancelled,
            $"{actor.Name} 撤销了换课申请", Describe(row), row.Id, ct);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>对方老师撤回强制换课：撤销调课记录，课表恢复原状；该申请人当天不能再强制换走这节课。</summary>
    public async Task<SwapRequestView> RevokeForcedAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await LoadForDecisionAsync(id, userId, SwapRequestStatus.Forced, ct);
        await adjustments.RevertBySwapAsync(row.Id, $"{actor.Name} 撤回了强制换课", ct);
        row.Status = SwapRequestStatus.Revoked;
        row.RevokedAt = DateTimeOffset.UtcNow;
        row.DecidedByUserId = userId;
        await db.SaveChangesAsync(ct);
        await PublishAsync(Classes(row), ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        await notifications.PublishAsync([row.RequesterUserId], UserNotificationKinds.SwapRevoked,
            $"{actor.Name} 撤回了你的强制换课",
            $"{Describe(row)}\n课表已恢复。当天这节课不能再强制换课，如仍需换课请提交普通申请。", row.Id, ct);
        await NotifyHomeroomAsync(row, actor.Name, ct, revoked: true);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>
    /// 相关班级的班主任或系统管理员撤回已生效的换课（含强制换课）：撤销调课记录，课表恢复。
    /// 调课记录由服务端掌握，撤回不依赖教室端当前课表，不会因课位已变化而卡住。
    /// </summary>
    public async Task<SwapRequestView> RevertAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await db.ScheduleSwapRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        if (!await CanRevertAsync(actor, row, ct)) throw NotFound();
        if (row.Status is not (SwapRequestStatus.Approved or SwapRequestStatus.Forced)) throw StateConflict(row);
        await adjustments.RevertBySwapAsync(row.Id, $"{actor.Name} 撤回了换课", ct);
        row.Status = SwapRequestStatus.Revoked;
        row.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await PublishAsync(Classes(row), ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        var recipients = Approvers(row).Append(row.RequesterUserId).Where(x => x != userId).ToList();
        await notifications.PublishAsync(recipients, UserNotificationKinds.SwapRevoked,
            $"{actor.Name} 撤回了换课", $"{Describe(row)}\n课表已恢复。", row.Id, ct);
        await NotifyHomeroomAsync(row, actor.Name, ct, revoked: true);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>
    /// 已生效的换课在教室端无法再应用（基础课表变化、放假、插件过旧等）：申请置为已失效，
    /// 撤掉同一申请的其余调课（不留半个换课），并通知申请人、对方老师与相关班主任。
    /// </summary>
    public async Task HandleInvalidatedAsync(IReadOnlyList<(Guid SwapRequestId, string Reason)> items, CancellationToken ct = default)
    {
        foreach (var (swapId, reason) in items.DistinctBy(x => x.SwapRequestId))
        {
            var row = await db.ScheduleSwapRequests.SingleOrDefaultAsync(x => x.Id == swapId, ct);
            // 审批中的申请由审批流程自己处理冲突（保持待审批并提示），这里只处理已生效的。
            if (row is null || row.Status is not (SwapRequestStatus.Approved or SwapRequestStatus.Forced)) continue;
            await adjustments.InvalidateBySwapAsync(row.Id, null, reason, ct);
            row.Status = SwapRequestStatus.Invalidated;
            row.InvalidReason = TrimNote(reason);
            await db.SaveChangesAsync(ct);
            await PublishAsync(Classes(row), ct);
            var recipients = new HashSet<Guid>(Approvers(row)) { row.RequesterUserId };
            foreach (var classId in Classes(row)) recipients.UnionWith(await HomeroomTeachersAsync(classId, ct));
            await notifications.PublishAsync(recipients, UserNotificationKinds.SwapInvalidated,
                "换课已失效", $"{Describe(row)}\n原因：{reason}\n课表已按教室端当前课表恢复，如仍需换课请重新申请。", row.Id, ct);
        }
    }

    /// <summary>后台清扫：日期已过仍未审批的申请标记为过期，日期已过的调课记录标记为过期。</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var pending = await db.ScheduleSwapRequests.Where(x => x.Status == SwapRequestStatus.Pending).ToListAsync(ct);
        foreach (var row in pending)
        {
            var expired = !IsSelectableDate(row.TargetDate, ClassToday(row.TargetClassId)) ||
                (row.SourceDate is { } date && row.SourceClassId is { } classId && !IsSelectableDate(date, ClassToday(classId)));
            if (!expired) continue;
            row.Status = SwapRequestStatus.Expired;
            foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        }
        await db.SaveChangesAsync(ct);
        await adjustments.SweepAsync(ct);
    }

    // ---------- 执行 ----------

    /// <summary>按最新课表复核课位，算出换课后每节课的科目与应上课老师。</summary>
    private async Task<List<SlotChange>> PlanChangesAsync(ScheduleSwapRequest row, Actor actor, CancellationToken ct)
    {
        var target = await ResolveCurrentAsync(row.TargetClassId, row.TargetDate, row.TargetIndex, row.TargetSubject, row.TargetTeacher, ct);
        if (row.Mode == SwapMode.Exchange)
        {
            var source = await ResolveCurrentAsync(row.SourceClassId!.Value, row.SourceDate!, row.SourceIndex!.Value,
                row.SourceSubject, row.SourceTeacher, ct);
            return
            [
                new SlotChange(source, FindSubject(source, target.Course.Subject), target.Course.Teacher),
                new SlotChange(target, FindSubject(target, source.Course.Subject), source.Course.Teacher),
            ];
        }
        var requester = actor.User.Id == row.RequesterUserId ? actor.Name : await RequesterNameAsync(row, ct);
        return [new SlotChange(target, FindSubject(target, row.ReplacementSubjectName!), requester)];
    }

    /// <summary>
    /// 写入调课记录并等待教室端回执：全部生效或教室端离线（待上线生效）都算成功；教室端报告冲突时撤掉这次的记录，
    /// 申请保持原状并返回 409。教室端插件在线但不支持调课集合时拒绝，提示先升级插件。
    /// </summary>
    private async Task ApplyAsync(ScheduleSwapRequest row, Actor actor, CancellationToken ct)
    {
        await ApplyGate.WaitAsync(ct);
        try
        {
            var changes = await PlanChangesAsync(row, actor, ct);
            foreach (var slot in changes.Select(x => x.Slot).DistinctBy(x => x.ClassId))
                if (channel.Supports(slot.ClassId) == false)
                    throw new SwapOperationException(ApiErrorCodes.CapabilityUnsupported,
                        $"{slot.Classroom.Name} 的教室端插件版本过旧，请先升级 RemoteCI 插件后再换课", StatusCodes.Status422UnprocessableEntity);
            var planned = changes.Select(change => new PlannedSlotChange(
                change.Slot.ClassId,
                change.Slot.Day.Date,
                change.Slot.Course.Index,
                change.Slot.Course.BaseSubjectId ?? change.Slot.Course.SubjectId,
                BaseSubjectName(change.Slot),
                change.To.Id,
                change.To.Name,
                OverrideTeacher(change))).ToList();
            var write = await adjustments.AddAsync(planned, ScheduleAdjustmentOrigin.SwapRequest, row.Id, actor.User.Id, actor.Name, ct);
            await adjustments.WaitForAcksAsync(write.Versions, ct);
            var ids = write.Rows.Select(x => x.Id).ToList();
            var failed = await db.ScheduleAdjustments.AsNoTracking()
                .Where(x => ids.Contains(x.Id) && x.Status == ScheduleAdjustmentStatus.Invalidated).FirstOrDefaultAsync(ct);
            if (failed is not null)
            {
                await adjustments.InvalidateBySwapAsync(row.Id, failed.ConflictCode, failed.StatusReason ?? "教室端无法应用", ct);
                await PublishAsync(changes.Select(x => x.Slot.ClassId), ct);
                throw new SwapOperationException(ApiErrorCodes.SwapSlotChanged,
                    $"教室端无法应用这次换课（{failed.StatusReason}），请刷新后重新申请", StatusCodes.Status409Conflict);
            }
            await PublishAsync(changes.Select(x => x.Slot.ClassId), ct);
        }
        finally
        {
            ApplyGate.Release();
        }
    }

    /// <summary>通过或强制生效后，触及同一节课的其他待审批申请失效并通知申请人。</summary>
    private async Task InvalidateSiblingsAsync(ScheduleSwapRequest row, CancellationToken ct)
    {
        var slots = new List<(Guid ClassId, string Date, int Index)> { (row.TargetClassId, row.TargetDate, row.TargetIndex) };
        if (row.SourceClassId is { } sourceClass) slots.Add((sourceClass, row.SourceDate!, row.SourceIndex!.Value));
        var pending = await db.ScheduleSwapRequests.Where(x => x.Status == SwapRequestStatus.Pending && x.Id != row.Id).ToListAsync(ct);
        var hits = pending.Where(x => slots.Any(slot => ScheduleConflictService.Touches(x, slot.ClassId, slot.Date, slot.Index))).ToList();
        if (hits.Count == 0) return;
        foreach (var hit in hits)
        {
            hit.Status = SwapRequestStatus.Invalidated;
            hit.InvalidReason = SiblingInvalidReason;
            hit.DecidedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        foreach (var hit in hits)
        {
            foreach (var approver in Approvers(hit)) await notifications.MarkSwapReadAsync(approver, hit.Id, ct);
            await notifications.PublishAsync([hit.RequesterUserId], UserNotificationKinds.SwapInvalidated,
                "你的换课申请已失效", $"{Describe(hit)}\n原因：{SiblingInvalidReason}，如仍需换课请重新申请。", hit.Id, ct);
        }
    }

    /// <summary>调课变化后立刻把叠加后的课表推给手机/手表，并刷新插件端授权镜像（临时任教影响班级访问）。</summary>
    private async Task PublishAsync(IEnumerable<Guid> classIds, CancellationToken ct)
    {
        foreach (var classId in classIds.Distinct())
        {
            if (state.GetLatestSchedule(classId) is { } bundle)
                await peers.SendScheduleToWatchesAsync(classId, bundle, ct);
        }
        try
        {
            await authorizationSync.SyncAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "换课后刷新授权镜像失败");
        }
    }

    /// <summary>新科目在该班的默认老师就是应上课老师时不需要临时任课老师。</summary>
    private static string? OverrideTeacher(SlotChange change) =>
        string.IsNullOrWhiteSpace(change.Teacher) || string.Equals(change.Teacher.Trim(), change.To.Teacher?.Trim(), StringComparison.Ordinal)
            ? null
            : change.Teacher.Trim();

    private static string BaseSubjectName(ResolvedSlot slot) =>
        slot.Course.BaseSubjectId is { } baseId
            ? slot.Bundle.Subjects.FirstOrDefault(x => x.Id == baseId)?.Name ?? slot.Course.Subject
            : slot.Course.Subject;

    private static List<PlannedLesson> ToLessons(IEnumerable<SlotChange> changes) => changes.Select(change => new PlannedLesson(
        change.Slot.ClassId,
        change.Slot.Classroom.Name,
        change.Slot.Day.Date,
        change.Slot.Course.Index,
        change.Slot.Course.Label,
        change.Slot.Course.StartTime,
        change.Slot.Course.EndTime,
        change.To.Name,
        string.IsNullOrWhiteSpace(change.Teacher) ? change.To.Teacher : change.Teacher)).ToList();

    private static void RequireAcknowledged(IReadOnlyList<SwapConflict> found, bool acknowledged)
    {
        if (acknowledged || !found.Any(x => x.RequiresAcknowledgement)) return;
        var lines = found.Where(x => x.RequiresAcknowledgement).Select(x => x.Message);
        throw new SwapOperationException(ApiErrorCodes.SwapConflicts,
            $"存在需要确认的冲突：{string.Join("；", lines)}。请在新版 App 或网页确认后继续。", StatusCodes.Status409Conflict)
        {
            Conflicts = found,
        };
    }

    private static string? SerializeAcknowledged(string? existing, IReadOnlyList<SwapConflict> found)
    {
        var items = ReadConflicts(existing).Concat(found.Where(x => x.RequiresAcknowledgement))
            .DistinctBy(x => x.Message).ToList();
        return items.Count == 0 ? null : JsonSerializer.Serialize(items, JsonDefaults.Options);
    }

    private static List<SwapConflict> ReadConflicts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<SwapConflict>>(json, JsonDefaults.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IEnumerable<Guid> Classes(ScheduleSwapRequest row) =>
        new[] { row.TargetClassId, row.SourceClassId ?? row.TargetClassId }.Distinct();

    // ---------- 校验与辅助 ----------

    private async Task<Actor> LoadActorAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(x => x.RoleDefinition).SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null || !user.Enabled)
            throw new SwapOperationException(ApiErrorCodes.Unauthorized, "账号不可用", StatusCodes.Status401Unauthorized);
        return new Actor(user, RolePermissions.Effective(user.Role, user.GrantedPermissions, user.RoleDefinition.DefaultPermissions));
    }

    private static void RequirePermission(Actor actor, UserPermissions permission)
    {
        if (!actor.Permissions.HasFlag(permission))
            throw new SwapOperationException(ApiErrorCodes.Forbidden,
                permission == UserPermissions.ForceScheduleSwap ? "没有强制换课权限" : "没有换课申请权限",
                StatusCodes.Status403Forbidden);
    }

    private async Task<ResolvedSlot> ResolveSlotAsync(SwapSlot input, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ClassId, ct)
            ?? throw Invalid("班级不存在");
        var bundle = state.GetLatestSchedule(classroom.Id)
            ?? throw Invalid($"{classroom.Name} 的课表尚未同步，请稍后再试");
        var day = bundle.Days.FirstOrDefault(x => x.Enabled && x.Date == input.Date);
        if (day is null || !IsSelectableDate(day.Date, ClassToday(classroom.Id)))
            throw Invalid($"{classroom.Name} {input.Date} 没有可换的课表");
        var course = day.Courses.FirstOrDefault(x => x.Enabled && x.Index == input.Index)
            ?? throw Invalid($"{classroom.Name} {input.Date} 没有这一节课");
        return new ResolvedSlot(classroom, bundle, day, course);
    }

    /// <summary>执行前复核：课位当前学科/老师必须仍是申请时的快照。</summary>
    private async Task<ResolvedSlot> ResolveCurrentAsync(
        Guid classId, string date, int index, string? expectedSubject, string? expectedTeacher, CancellationToken ct)
    {
        ResolvedSlot slot;
        try
        {
            slot = await ResolveSlotAsync(new SwapSlot { ClassId = classId, Date = date, Index = index }, ct);
        }
        catch (SwapOperationException ex)
        {
            throw new SwapOperationException(ApiErrorCodes.SwapSlotChanged, ex.Message, StatusCodes.Status409Conflict);
        }
        if (!string.Equals(slot.Course.Subject, expectedSubject, StringComparison.Ordinal) ||
            (expectedTeacher is not null && !string.Equals(slot.Course.Teacher, expectedTeacher, StringComparison.Ordinal)))
            throw new SwapOperationException(ApiErrorCodes.SwapSlotChanged,
                $"{slot.Classroom.Name} {date} {slot.Course.Label} 已被调整，请重新申请", StatusCodes.Status409Conflict);
        return slot;
    }

    private static void RequireSubject(ResolvedSlot slot, string subjectName) => FindSubject(slot, subjectName);

    private static SubjectEntry FindSubject(ResolvedSlot slot, string subjectName) =>
        slot.Bundle.Subjects.FirstOrDefault(x => string.Equals(x.Name, subjectName, StringComparison.Ordinal))
        ?? throw new SwapOperationException(ApiErrorCodes.SwapSubjectMissing,
            $"{slot.Classroom.Name} 没有“{subjectName}”这门学科，无法换入");

    /// <summary>
    /// 审批人：对方课老师字段匹配到的启用账号；匹配不到时回退为该班班主任，再回退为系统管理员。
    /// 申请人本人始终排除在外。
    /// </summary>
    private async Task<List<Guid>> ResolveApproversAsync(ResolvedSlot counterpart, Guid requesterId, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(x => x.Enabled && x.Id != requesterId)
            .Select(x => new { x.Id, x.DisplayName, x.Role }).ToListAsync(ct);
        var teachers = users.Where(x => TeacherBindingService.Matches(x.DisplayName, counterpart.Course.Teacher))
            .Select(x => x.Id).ToList();
        if (teachers.Count > 0) return teachers;
        var homeroom = await HomeroomTeachersAsync(counterpart.ClassId, ct);
        homeroom.Remove(requesterId);
        if (homeroom.Count > 0) return [.. homeroom];
        return users.Where(x => x.Role == UserRole.Admin).Select(x => x.Id).ToList();
    }

    private async Task<HashSet<Guid>> HomeroomTeachersAsync(Guid classId, CancellationToken ct) =>
        (await db.ClassMemberships.AsNoTracking()
            .Where(x => x.ClassroomId == classId && x.User.Enabled)
            .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => new { x.UserId, y.Kind })
            .Where(x => x.Kind == AccountRoleKind.ClassAdministrator)
            .Select(x => x.UserId)
            .ToListAsync(ct)).ToHashSet();

    /// <summary>换课生效或撤回后通知相关班级的班主任（申请人与审批人本人除外）。</summary>
    private async Task NotifyHomeroomAsync(ScheduleSwapRequest row, string actorName, CancellationToken ct, bool revoked = false)
    {
        var classIds = new[] { row.TargetClassId, row.SourceClassId ?? row.TargetClassId }.Distinct();
        var recipients = new HashSet<Guid>();
        foreach (var classId in classIds) recipients.UnionWith(await HomeroomTeachersAsync(classId, ct));
        recipients.Remove(row.RequesterUserId);
        if (row.DecidedByUserId is { } decidedBy) recipients.Remove(decidedBy);
        if (recipients.Count == 0) return;
        var requester = await RequesterNameAsync(row, ct);
        var title = revoked
            ? $"{actorName} 撤回了 {requester} 的强制换课"
            : row.Forced ? $"{requester} 强制换课" : $"{requester} 的换课已生效";
        await notifications.PublishAsync(recipients, UserNotificationKinds.SwapHomeroomInfo, title,
            $"{Describe(row)}\n理由：{row.Reason}" + (revoked ? "\n课表已恢复。" : "\n课表已临时调整。"), row.Id, ct);
    }

    private async Task<ScheduleSwapRequest> LoadForDecisionAsync(
        Guid id, Guid userId, SwapRequestStatus expected, CancellationToken ct)
    {
        var row = await db.ScheduleSwapRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        if (!Approvers(row).Contains(userId)) throw NotFound();
        if (row.Status != expected) throw StateConflict(row);
        return row;
    }

    private async Task<bool> IsForceLockedAsync(ScheduleSwapRequest row, CancellationToken ct) =>
        await db.ScheduleSwapRequests.AnyAsync(x =>
            x.RequesterUserId == row.RequesterUserId && x.Forced && x.Status == SwapRequestStatus.Revoked &&
            x.CounterpartClassId == row.CounterpartClassId && x.CounterpartDate == row.CounterpartDate &&
            x.CounterpartIndex == row.CounterpartIndex, ct);

    /// <summary>相同的待审批申请：不论谁发起、不论两节课的先后顺序，同一对课位、同一模式与换入学科只保留一份。</summary>
    private async Task<bool> HasDuplicatePendingAsync(ScheduleSwapRequest row, CancellationToken ct)
    {
        var pending = await db.ScheduleSwapRequests.AsNoTracking()
            .Where(x => x.Status == SwapRequestStatus.Pending && x.Mode == row.Mode &&
                        (x.TargetClassId == row.TargetClassId || x.SourceClassId == row.TargetClassId))
            .ToListAsync(ct);
        var slots = SlotSet(row);
        return pending.Any(x => SlotSet(x).SetEquals(slots) &&
            string.Equals(x.ReplacementSubjectName, row.ReplacementSubjectName, StringComparison.Ordinal));
    }

    private static HashSet<(Guid, string, int)> SlotSet(ScheduleSwapRequest row)
    {
        var slots = new HashSet<(Guid, string, int)> { (row.TargetClassId, row.TargetDate, row.TargetIndex) };
        if (row.SourceClassId is { } sourceClass) slots.Add((sourceClass, row.SourceDate!, row.SourceIndex!.Value));
        return slots;
    }

    private async Task<string> RequesterNameAsync(ScheduleSwapRequest row, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(x => x.Id == row.RequesterUserId)
            .Select(x => x.DisplayName).SingleOrDefaultAsync(ct) ?? "未知老师";

    /// <summary>系统管理员或涉及班级的班主任可以撤回已生效的换课。</summary>
    private async Task<bool> CanRevertAsync(Actor actor, ScheduleSwapRequest row, CancellationToken ct)
    {
        if (actor.User.Role == UserRole.Admin) return true;
        var homeroom = await HomeroomClassesAsync(actor.User.Id, ct);
        return Classes(row).Any(homeroom.Contains);
    }

    private async Task<HashSet<Guid>> HomeroomClassesAsync(Guid userId, CancellationToken ct) =>
        (await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => new { x.ClassroomId, y.Kind })
            .Where(x => x.Kind == AccountRoleKind.ClassAdministrator)
            .Select(x => x.ClassroomId)
            .ToListAsync(ct)).ToHashSet();

    private async Task<IReadOnlyList<SwapRequestView>> ToViewsAsync(
        IReadOnlyList<ScheduleSwapRequest> rows, Actor actor, CancellationToken ct)
    {
        var userIds = rows.SelectMany(x => Approvers(x).Append(x.RequesterUserId)
            .Concat(x.DecidedByUserId is { } d ? [d] : [])).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.UserName ?? "" : x.DisplayName, ct);
        var classIds = rows.SelectMany(x => new[] { x.TargetClassId, x.SourceClassId ?? x.TargetClassId }).Distinct().ToList();
        var classNames = await db.Classrooms.AsNoTracking().Where(x => classIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var forcedIds = rows.Where(x => x.Status == SwapRequestStatus.Forced).Select(x => x.Id).ToList();
        var unseenForced = forcedIds.Count == 0 ? [] : (await db.UserNotifications.AsNoTracking()
            .Where(x => x.UserId == actor.User.Id && x.Kind == UserNotificationKinds.SwapForced && x.ReadAt == null &&
                x.SwapRequestId != null && forcedIds.Contains(x.SwapRequestId.Value))
            .Select(x => x.SwapRequestId!.Value).ToListAsync(ct)).ToHashSet();
        var liveIds = rows.Where(x => x.Status is SwapRequestStatus.Approved or SwapRequestStatus.Forced).Select(x => x.Id).ToList();
        var deliveries = liveIds.Count == 0 ? [] : (await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.SwapRequestId != null && liveIds.Contains(x.SwapRequestId.Value) && x.Status == ScheduleAdjustmentStatus.Active)
            .Select(x => new { SwapRequestId = x.SwapRequestId!.Value, x.Delivery }).ToListAsync(ct))
            .GroupBy(x => x.SwapRequestId)
            .ToDictionary(x => x.Key, x => x.All(y => y.Delivery == ScheduleAdjustmentDelivery.Applied)
                ? SwapDeliveryStates.Applied : SwapDeliveryStates.Pending);
        var homeroom = actor.User.Role == UserRole.Admin ? null : await HomeroomClassesAsync(actor.User.Id, ct);
        return rows.Select(row =>
        {
            var approvers = Approvers(row);
            var isApprover = approvers.Contains(actor.User.Id);
            var canDecide = isApprover && row.Status == SwapRequestStatus.Pending;
            var canRevoke = isApprover && row.Status == SwapRequestStatus.Forced;
            var live = row.Status is SwapRequestStatus.Approved or SwapRequestStatus.Forced;
            return new SwapRequestView
            {
                Id = row.Id,
                ShortId = row.Id.ToString("N")[..8],
                Mode = row.Mode,
                Status = row.Status,
                Forced = row.Forced,
                RequesterUserId = row.RequesterUserId,
                RequesterName = names.GetValueOrDefault(row.RequesterUserId, "未知老师"),
                Source = row.SourceClassId is { } sourceClass
                    ? BuildSlotView(sourceClass, row.SourceDate!, row.SourceIndex!.Value, row.SourceSubject, row.SourceTeacher, classNames)
                    : null,
                Target = BuildSlotView(row.TargetClassId, row.TargetDate, row.TargetIndex, row.TargetSubject, row.TargetTeacher, classNames),
                SubjectName = row.ReplacementSubjectName,
                Reason = row.Reason,
                ApproverNames = approvers.Select(x => names.GetValueOrDefault(x, "")).Where(x => x.Length > 0).ToList(),
                DecidedByName = row.DecidedByUserId is { } decided ? names.GetValueOrDefault(decided) : null,
                DecisionNote = row.DecisionNote,
                CreatedAt = row.CreatedAt,
                DecidedAt = row.DecidedAt,
                CanDecide = canDecide,
                CanRevoke = canRevoke,
                NeedsAction = canDecide || canRevoke && unseenForced.Contains(row.Id),
                CanCancel = row.RequesterUserId == actor.User.Id && row.Status == SwapRequestStatus.Pending,
                CanRevert = live && (homeroom is null || Classes(row).Any(homeroom.Contains)),
                // 旧版换课（逐节命令时代）没有调课记录，按已生效显示。
                Delivery = live ? deliveries.GetValueOrDefault(row.Id, SwapDeliveryStates.Applied) : null,
                InvalidReason = row.Status == SwapRequestStatus.Invalidated ? row.InvalidReason : null,
                AcknowledgedConflicts = ReadConflicts(row.AcknowledgedConflictsJson),
            };
        }).ToList();
    }

    private SwapSlot BuildSlotView(Guid classId, string date, int index, string? subject, string? teacher,
        IReadOnlyDictionary<Guid, string> classNames)
    {
        var course = state.GetLatestSchedule(classId)?.Days.FirstOrDefault(x => x.Date == date)?.Courses
            .FirstOrDefault(x => x.Index == index);
        return new SwapSlot
        {
            ClassId = classId,
            Date = date,
            Index = index,
            ClassName = classNames.GetValueOrDefault(classId),
            Label = course?.Label ?? $"第 {index + 1} 节",
            Subject = subject,
            Teacher = teacher,
            StartTime = course?.StartTime,
            EndTime = course?.EndTime,
        };
    }

    /// <summary>通知正文里的一行课位描述，例如“高一1班 10-06 第3节 数学(张三) ⇄ 高一2班 10-07 第1节 英语(李四)”。</summary>
    private string Describe(ScheduleSwapRequest row)
    {
        string Slot(Guid classId, string date, int index, string? subject, string? teacher)
        {
            var className = db.Classrooms.Local.FirstOrDefault(x => x.Id == classId)?.Name
                ?? db.Classrooms.AsNoTracking().Where(x => x.Id == classId).Select(x => x.Name).FirstOrDefault() ?? "";
            var label = state.GetLatestSchedule(classId)?.Days.FirstOrDefault(x => x.Date == date)?.Courses
                .FirstOrDefault(x => x.Index == index)?.Label ?? $"第{index + 1}节";
            var teacherText = string.IsNullOrWhiteSpace(teacher) ? "" : $"({teacher})";
            return $"{className} {ShortDate(date)} {label} {subject}{teacherText}";
        }
        var target = Slot(row.TargetClassId, row.TargetDate, row.TargetIndex, row.TargetSubject, row.TargetTeacher);
        return row.Mode == SwapMode.Exchange
            ? $"{Slot(row.SourceClassId!.Value, row.SourceDate!, row.SourceIndex!.Value, row.SourceSubject, row.SourceTeacher)} ⇄ {target}"
            : $"{target} → {row.ReplacementSubjectName}";
    }

    private static string ShortDate(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("MM-dd", CultureInfo.InvariantCulture)
            : date;

    /// <summary>教室端本地“今天”。</summary>
    private DateOnly ClassToday(Guid classId) => ClassClock.Today(state, classId);

    /// <summary>插件只接受今天起 30 天内的换课（与上报的课表天数一致）。</summary>
    private static bool IsSelectableDate(string date, DateOnly today) => IsWithinDays(date, today, ScheduleBundle.HorizonDays);

    private static bool IsWithinDays(string date, DateOnly today, int days) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) &&
        value >= today && value < today.AddDays(days);

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<Guid> Approvers(ScheduleSwapRequest row) =>
        JsonSerializer.Deserialize<List<Guid>>(row.ApproverUserIdsJson) ?? [];

    private static string? TrimNote(string? note)
    {
        var value = note?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length > MaxReasonLength ? value[..MaxReasonLength] : value;
    }

    private static string NoteSuffix(string? note) => string.IsNullOrEmpty(note) ? "" : $"\n备注：{note}";

    private static SwapOperationException Invalid(string message) => new(ApiErrorCodes.InvalidRequest, message);

    private static SwapOperationException NotFound() =>
        new(ApiErrorCodes.NotFound, "换课申请不存在", StatusCodes.Status404NotFound);

    private static SwapOperationException StateConflict(ScheduleSwapRequest row) => new(ApiErrorCodes.SwapStateConflict,
        row.Status switch
        {
            SwapRequestStatus.Approved => "该申请已通过",
            SwapRequestStatus.Rejected => "该申请已被拒绝",
            SwapRequestStatus.Cancelled => "该申请已撤销",
            SwapRequestStatus.Expired => "该申请已过期",
            SwapRequestStatus.Forced => "该换课已强制生效",
            SwapRequestStatus.Revoked => "该换课已撤回",
            SwapRequestStatus.Invalidated => "该申请已失效",
            _ => "申请状态已变化",
        }, StatusCodes.Status409Conflict);
}
