using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Data;

/// <summary>
/// 老师发起的换课申请（一律为临时换课）。课位字段是申请时快照，执行前按最新课表复核。
/// 替换模式没有 Source*，Target 课位会被替换为申请人任教的 ReplacementSubjectName。
/// </summary>
public sealed class ScheduleSwapRequest
{
    public Guid Id { get; set; }
    public SwapMode Mode { get; set; }
    public SwapRequestStatus Status { get; set; }
    public bool Forced { get; set; }

    public Guid RequesterUserId { get; set; }
    public AppUser Requester { get; set; } = null!;
    public string Reason { get; set; } = string.Empty;

    public Guid? SourceClassId { get; set; }
    public string? SourceDate { get; set; }
    public int? SourceIndex { get; set; }
    public string? SourceSubject { get; set; }
    public string? SourceTeacher { get; set; }

    public Guid TargetClassId { get; set; }
    public string TargetDate { get; set; } = string.Empty;
    public int TargetIndex { get; set; }
    public string TargetSubject { get; set; } = string.Empty;
    public string? TargetTeacher { get; set; }

    public string? ReplacementSubjectName { get; set; }

    /// <summary>可审批（或撤回强制换课）的用户 Id，JSON 数组；对方老师或回退的班主任。</summary>
    public string ApproverUserIdsJson { get; set; } = "[]";

    /// <summary>强制换课时“对方”的课位（撤回后锁定用）；Exchange 为非申请人的那一节，Replace 为目标课。</summary>
    public Guid CounterpartClassId { get; set; }
    public string CounterpartDate { get; set; } = string.Empty;
    public int CounterpartIndex { get; set; }

    public Guid? DecidedByUserId { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// 遗留字段：旧版逐节命令下发的改动 JSON。新版改为 <see cref="ScheduleAdjustment"/>（按 SwapRequestId 关联），
    /// 只在升级时回填一次，之后不再写入。
    /// </summary>
    public string? AppliedPlanJson { get; set; }

    /// <summary>失效原因（Status 为 Invalidated 时）。</summary>
    public string? InvalidReason { get; set; }

    /// <summary>提交或审批时确认过的冲突提示，JSON 数组（SwapConflict）。</summary>
    public string? AcknowledgedConflictsJson { get; set; }
}

/// <summary>
/// 遗留表：旧版单节课临时任课老师覆盖。新版由 <see cref="ScheduleAdjustment.TeacherName"/> 取代，
/// 只在升级时回填一次，之后不再读写。
/// </summary>
public sealed class LessonTeacherOverride
{
    public Guid Id { get; set; }
    public Guid ClassId { get; set; }
    public string Date { get; set; } = string.Empty;
    public int Index { get; set; }
    public string TeacherName { get; set; } = string.Empty;
    public string ExpectedSubject { get; set; } = string.Empty;
    public Guid SwapRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>个人通知：WebUI 铃铛、手机通知、AstrBot 轮询共用同一份记录。</summary>
public sealed class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public Guid? SwapRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    /// <summary>相关班级（设备告警等），可空。</summary>
    public Guid? ClassId { get; set; }
    /// <summary>点击通知打开的站内相对路径；为空时按通知种类选择默认页面。</summary>
    public string? Link { get; set; }
}

/// <summary>浏览器 Web Push 订阅；关闭网页后仍可收到系统通知。</summary>
public sealed class WebPushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ClassId { get; set; }
    // Existing desktop subscriptions receive personal notifications only.
    public int EventMask { get; set; }
    public string? BrowserId { get; set; }
}
