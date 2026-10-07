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
}

/// <summary>
/// 老师换课申请：老师发起（互换两节课或把目标课替换为自己任教的学科），对方老师审批后
/// 以临时换课写入教室端；开启强制换课权限的老师可直接生效，对方老师可撤回。
/// 课表不落库，执行前总是按服务端内存中的最新课表复核课位，避免把过期的选择写进教室端。
/// 永久换课仍由班主任在课表页操作，这里只产生临时（按日期）的改动。
/// </summary>
public sealed class ScheduleSwapService(
    AppDbContext db,
    IStateStore state,
    LessonOverrideTable overrides,
    PeerRegistry peers,
    IScheduleCommandSender commands,
    UserNotificationService notifications,
    AuthorizationSyncService authorizationSync,
    ILogger<ScheduleSwapService> logger)
{
    public const int MaxReasonLength = 200;
    public const string ForceWarning = "仅在需要紧急换课时使用，请提前与对方沟通并达成一致。";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>同一时间只执行一个换课：两条命令（跨班/跨日）之间不能插入其他换课。</summary>
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);

    /// <summary>实际写入教室端的一节课改动；撤回时按 FromSubjectId 反向替换。</summary>
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
            var item = new SwapCatalogClass
            {
                ClassId = classroom.Id,
                ClassName = classroom.Name,
                Subjects = bundle.Subjects.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).ToList(),
            };
            foreach (var subject in bundle.Subjects.Where(x => TeacherBindingService.Matches(actor.Name, x.Teacher)))
                mySubjects.Add(subject.Name);
            foreach (var day in bundle.Days.Where(x => x.Enabled && IsSelectableDate(x.Date, today)))
            {
                var courses = day.Courses.Where(x => x.Enabled).OrderBy(x => x.Index).Select(course =>
                {
                    var mine = TeacherBindingService.Matches(actor.Name, course.Teacher);
                    if (mine && !string.IsNullOrWhiteSpace(course.Subject)) mySubjects.Add(course.Subject);
                    return new SwapCatalogCourse
                    {
                        Index = course.Index,
                        Label = course.Label,
                        Subject = course.Subject,
                        Teacher = course.Teacher,
                        StartTime = course.StartTime,
                        EndTime = course.EndTime,
                        Mine = mine,
                    };
                }).ToList();
                if (courses.Count > 0) item.Days.Add(new SwapCatalogDay { Date = day.Date, Courses = courses });
            }
            if (item.Days.Count > 0) catalog.Classes.Add(item);
        }
        catalog.MySubjects = [.. mySubjects];
        return catalog;
    }

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
        if (row.RequesterUserId != userId && !Approvers(row).Contains(userId) && actor.User.Role != UserRole.Admin)
            throw NotFound();
        return (await ToViewsAsync([row], actor, ct))[0];
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

    public Task<int> CountPendingForAsync(Guid userId, CancellationToken ct = default)
    {
        var userKey = userId.ToString();
        return db.ScheduleSwapRequests.CountAsync(x =>
            x.Status == SwapRequestStatus.Pending && x.ApproverUserIdsJson.Contains(userKey), ct);
    }

    // ---------- 申请 ----------

    public async Task<SwapRequestView> CreateAsync(Guid userId, CreateSwapRequest request, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
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

        if (request.Force && counterpart is not null && await IsForceLockedAsync(row, ct))
            throw new SwapOperationException(ApiErrorCodes.SwapForceLocked,
                "你当天对这节课的强制换课已被对方撤回，请提交普通换课申请", StatusCodes.Status409Conflict);
        if (await HasDuplicatePendingAsync(row, ct))
            throw new SwapOperationException(ApiErrorCodes.SwapStateConflict,
                "已有相同的待审批换课申请", StatusCodes.Status409Conflict);

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

    public async Task<SwapRequestView> ApproveAsync(Guid userId, Guid id, string? note, CancellationToken ct = default)
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

    /// <summary>对方老师撤回强制换课：课表恢复原状；该申请人当天不能再强制换走这节课。</summary>
    public async Task<SwapRequestView> RevokeForcedAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var actor = await LoadActorAsync(userId, ct);
        var row = await LoadForDecisionAsync(id, userId, SwapRequestStatus.Forced, ct);
        await RevertAsync(row, actor, ct);
        row.Status = SwapRequestStatus.Revoked;
        row.RevokedAt = DateTimeOffset.UtcNow;
        row.DecidedByUserId = userId;
        await db.SaveChangesAsync(ct);
        foreach (var approver in Approvers(row)) await notifications.MarkSwapReadAsync(approver, row.Id, ct);
        await notifications.PublishAsync([row.RequesterUserId], UserNotificationKinds.SwapRevoked,
            $"{actor.Name} 撤回了你的强制换课",
            $"{Describe(row)}\n课表已恢复。当天这节课不能再强制换课，如仍需换课请提交普通申请。", row.Id, ct);
        await NotifyHomeroomAsync(row, actor.Name, ct, revoked: true);
        return (await ToViewsAsync([row], actor, ct))[0];
    }

    /// <summary>后台清扫：日期已过仍未审批的申请标记为过期，并清理过期的临时任课老师覆盖。</summary>
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
        var stale = (await db.LessonTeacherOverrides.ToListAsync(ct))
            .Where(x => !IsSelectableDate(x.Date, ClassToday(x.ClassId)))
            .ToList();
        db.LessonTeacherOverrides.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        foreach (var item in stale) overrides.Remove(item.ClassId, item.Date, item.Index);
    }

    /// <summary>启动时把数据库中的临时任课老师覆盖加载到内存。</summary>
    public async Task LoadOverridesAsync(CancellationToken ct = default)
    {
        var rows = await db.LessonTeacherOverrides.AsNoTracking().ToListAsync(ct);
        overrides.ReplaceAll(rows.Select(x => new LessonOverride(x.ClassId, x.Date, x.Index, x.TeacherName, x.ExpectedSubject)));
    }

    // ---------- 执行 ----------

    private async Task ApplyAsync(ScheduleSwapRequest row, Actor actor, CancellationToken ct)
    {
        await ApplyGate.WaitAsync(ct);
        try
        {
            var target = await ResolveCurrentAsync(row.TargetClassId, row.TargetDate, row.TargetIndex, row.TargetSubject, row.TargetTeacher, ct);
            var changes = new List<(ResolvedSlot Slot, SubjectEntry To, string? Teacher)>();
            if (row.Mode == SwapMode.Exchange)
            {
                var source = await ResolveCurrentAsync(row.SourceClassId!.Value, row.SourceDate!, row.SourceIndex!.Value,
                    row.SourceSubject, row.SourceTeacher, ct);
                changes.Add((source, FindSubject(source, target.Course.Subject), target.Course.Teacher));
                changes.Add((target, FindSubject(target, source.Course.Subject), source.Course.Teacher));
                if (source.ClassId == target.ClassId && source.Day.Date == target.Day.Date)
                {
                    // 同班同日：一条 Exchange 命令原子完成，学科连同默认老师一起交换。
                    var result = await SendChangeAsync(source.ClassId, new ScheduleChangeRequest
                    {
                        Date = source.Day.Date,
                        Mode = ScheduleChangeMode.Exchange,
                        SourceIndex = source.Course.Index,
                        TargetIndex = target.Course.Index,
                        ExpectedRevision = source.Day.Revision,
                    }, actor, ct);
                    if (!result.Success) throw CommandFailure(result);
                    await CommitChangesAsync(row, changes, ct);
                    return;
                }
            }
            else
            {
                var subject = FindSubject(target, row.ReplacementSubjectName!);
                changes.Add((target, subject, actor.User.Id == row.RequesterUserId
                    ? actor.Name
                    : await RequesterNameAsync(row, ct)));
            }

            // 跨班或跨日：逐节 Replace；后一条失败时把已生效的前几条反向替换回去。
            var done = new List<(ResolvedSlot Slot, string Revision)>();
            foreach (var change in changes)
            {
                var revision = done.LastOrDefault(x => x.Slot.ClassId == change.Slot.ClassId && x.Slot.Day.Date == change.Slot.Day.Date)
                    .Revision ?? change.Slot.Day.Revision;
                var result = await SendChangeAsync(change.Slot.ClassId, new ScheduleChangeRequest
                {
                    Date = change.Slot.Day.Date,
                    Mode = ScheduleChangeMode.Replace,
                    SourceIndex = change.Slot.Course.Index,
                    ReplacementSubjectId = change.To.Id,
                    ExpectedRevision = revision,
                }, actor, ct);
                if (!result.Success)
                {
                    await CompensateAsync(done, actor);
                    throw CommandFailure(result);
                }
                done.Add((change.Slot, result.ScheduleRevision ?? string.Empty));
            }
            await CommitChangesAsync(row, changes, ct);
        }
        finally
        {
            ApplyGate.Release();
        }
    }

    private async Task CompensateAsync(IEnumerable<(ResolvedSlot Slot, string Revision)> done, Actor actor)
    {
        foreach (var (slot, revision) in done.Reverse())
        {
            var result = await SendChangeAsync(slot.ClassId, new ScheduleChangeRequest
            {
                Date = slot.Day.Date,
                Mode = ScheduleChangeMode.Replace,
                SourceIndex = slot.Course.Index,
                ReplacementSubjectId = slot.Course.SubjectId,
                ExpectedRevision = revision,
            }, actor, CancellationToken.None);
            if (!result.Success)
                logger.LogWarning("换课补偿失败：班级 {ClassId} {Date} 第 {Index} 节未能恢复：{Message}",
                    slot.ClassId, slot.Day.Date, slot.Course.Index, result.Message);
        }
    }

    /// <summary>记录改动并写入临时任课老师覆盖：新学科在该班的默认老师不是应上课老师时覆盖为应上课老师。</summary>
    private async Task CommitChangesAsync(
        ScheduleSwapRequest row, IReadOnlyList<(ResolvedSlot Slot, SubjectEntry To, string? Teacher)> changes, CancellationToken ct)
    {
        var applied = new List<AppliedChange>();
        foreach (var (slot, to, teacher) in changes)
        {
            var existing = await db.LessonTeacherOverrides.SingleOrDefaultAsync(x =>
                x.ClassId == slot.ClassId && x.Date == slot.Day.Date && x.Index == slot.Course.Index, ct);
            var desired = string.IsNullOrWhiteSpace(teacher) || string.Equals(teacher.Trim(), to.Teacher?.Trim(), StringComparison.Ordinal)
                ? null
                : teacher.Trim();
            applied.Add(new AppliedChange(slot.ClassId, slot.Day.Date, slot.Course.Index, slot.Course.SubjectId,
                slot.Course.Subject, to.Name, existing?.TeacherName, existing?.ExpectedSubject, desired));
            SetOverride(existing, slot.ClassId, slot.Day.Date, slot.Course.Index, desired, to.Name, row.Id);
        }
        row.AppliedPlanJson = JsonSerializer.Serialize(applied);
        await db.SaveChangesAsync(ct);
        await PublishOverlaidSchedulesAsync(changes.Select(x => x.Slot.ClassId), ct);
    }

    private async Task RevertAsync(ScheduleSwapRequest row, Actor actor, CancellationToken ct)
    {
        var applied = string.IsNullOrEmpty(row.AppliedPlanJson)
            ? []
            : JsonSerializer.Deserialize<List<AppliedChange>>(row.AppliedPlanJson) ?? [];
        await ApplyGate.WaitAsync(ct);
        try
        {
            var revisions = new Dictionary<(Guid, string), string>();
            foreach (var change in applied)
            {
                var slot = await ResolveCurrentAsync(change.ClassId, change.Date, change.Index, change.ToSubject, null, ct);
                var revision = revisions.GetValueOrDefault((change.ClassId, change.Date)) ?? slot.Day.Revision;
                var result = await SendChangeAsync(change.ClassId, new ScheduleChangeRequest
                {
                    Date = change.Date,
                    Mode = ScheduleChangeMode.Replace,
                    SourceIndex = change.Index,
                    ReplacementSubjectId = change.FromSubjectId,
                    ExpectedRevision = revision,
                }, actor, ct);
                if (!result.Success) throw CommandFailure(result);
                revisions[(change.ClassId, change.Date)] = result.ScheduleRevision ?? string.Empty;
                var existing = await db.LessonTeacherOverrides.SingleOrDefaultAsync(x =>
                    x.ClassId == change.ClassId && x.Date == change.Date && x.Index == change.Index, ct);
                SetOverride(existing, change.ClassId, change.Date, change.Index,
                    change.PreviousOverrideTeacher, change.PreviousOverrideSubject ?? change.FromSubject, row.Id);
            }
            await db.SaveChangesAsync(ct);
            await PublishOverlaidSchedulesAsync(applied.Select(x => x.ClassId), ct);
        }
        finally
        {
            ApplyGate.Release();
        }
    }

    private void SetOverride(LessonTeacherOverride? existing, Guid classId, string date, int index,
        string? teacher, string expectedSubject, Guid swapRequestId)
    {
        if (teacher is null)
        {
            if (existing is not null) db.LessonTeacherOverrides.Remove(existing);
            overrides.Remove(classId, date, index);
            return;
        }
        if (existing is null)
        {
            existing = new LessonTeacherOverride
            {
                Id = Guid.NewGuid(),
                ClassId = classId,
                Date = date,
                Index = index,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.LessonTeacherOverrides.Add(existing);
        }
        existing.TeacherName = teacher;
        existing.ExpectedSubject = expectedSubject;
        existing.SwapRequestId = swapRequestId;
        overrides.Set(new LessonOverride(classId, date, index, teacher, expectedSubject));
    }

    /// <summary>覆盖变化后立刻把叠加后的课表推给手机/手表，并刷新插件端授权镜像（临时任教影响班级访问）。</summary>
    private async Task PublishOverlaidSchedulesAsync(IEnumerable<Guid> classIds, CancellationToken ct)
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

    private Task<CommandResult> SendChangeAsync(Guid classId, ScheduleChangeRequest change, Actor actor, CancellationToken ct) =>
        commands.SendAsync(classId, new CommandMessage
        {
            Command = CommandKind.ChangeSchedule,
            ClassId = classId,
            // 换课申请由服务端代为执行：老师本身没有 ManageSchedule，执行身份只携带换课所需权限位，
            // 显示名标注来源，便于教室端日志审计。
            RequestedBy = new UserProfile
            {
                Id = actor.User.Id,
                Username = actor.User.UserName ?? string.Empty,
                DisplayName = $"换课申请 · {actor.Name}",
                Role = UserRole.User,
                GrantedPermissions = UserPermissions.None,
                Permissions = UserPermissions.ViewCurrentCourse | UserPermissions.ManageSchedule,
                Version = actor.User.Version,
            },
            ScheduleChange = change,
        }, CommandTimeout, ct);

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

    private Task<bool> HasDuplicatePendingAsync(ScheduleSwapRequest row, CancellationToken ct) =>
        db.ScheduleSwapRequests.AnyAsync(x =>
            x.RequesterUserId == row.RequesterUserId && x.Status == SwapRequestStatus.Pending && x.Mode == row.Mode &&
            x.TargetClassId == row.TargetClassId && x.TargetDate == row.TargetDate && x.TargetIndex == row.TargetIndex &&
            x.SourceClassId == row.SourceClassId && x.SourceDate == row.SourceDate && x.SourceIndex == row.SourceIndex, ct);

    private async Task<string> RequesterNameAsync(ScheduleSwapRequest row, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(x => x.Id == row.RequesterUserId)
            .Select(x => x.DisplayName).SingleOrDefaultAsync(ct) ?? "未知老师";

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
        return rows.Select(row =>
        {
            var approvers = Approvers(row);
            var isApprover = approvers.Contains(actor.User.Id);
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
                CanDecide = isApprover && row.Status == SwapRequestStatus.Pending,
                CanRevoke = isApprover && row.Status == SwapRequestStatus.Forced,
                CanCancel = row.RequesterUserId == actor.User.Id && row.Status == SwapRequestStatus.Pending,
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

    /// <summary>教室端本地“今天”：按该班最近状态快照的时区偏移；没有快照时用服务端本地时区。</summary>
    private DateOnly ClassToday(Guid classId)
    {
        var now = DateTimeOffset.UtcNow;
        var offset = state.GetLatestSnapshot(classId)?.TimeZoneOffsetMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : TimeZoneInfo.Local.GetUtcOffset(now);
        return DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
    }

    /// <summary>插件只接受今天起 7 天内的换课。</summary>
    private static bool IsSelectableDate(string date, DateOnly today) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) &&
        value >= today && value <= today.AddDays(6);

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
            SwapRequestStatus.Revoked => "该强制换课已撤回",
            _ => "申请状态已变化",
        }, StatusCodes.Status409Conflict);

    private static SwapOperationException CommandFailure(CommandResult result) => new(
        result.Code == CommandResultCodes.ScheduleStale ? ApiErrorCodes.SwapSlotChanged : result.Code,
        result.Code == CommandResultCodes.ScheduleStale ? "课表刚刚被修改，请重新申请" : result.Message,
        result.Code switch
        {
            CommandResultCodes.PluginOffline => StatusCodes.Status503ServiceUnavailable,
            CommandResultCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            CommandResultCodes.ScheduleStale => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        });
}
