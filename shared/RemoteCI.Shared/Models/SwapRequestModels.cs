using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>换课申请模式：互换两节课，或只把目标课替换为申请人任教的某学科。</summary>
public enum SwapMode
{
    Exchange = 1,
    Replace = 2,
}

public enum SwapRequestStatus
{
    /// <summary>等待对方老师（或回退的班主任）审批。</summary>
    Pending = 1,
    /// <summary>审批通过且已在教室端生效。</summary>
    Approved = 2,
    Rejected = 3,
    /// <summary>申请人在审批前撤销。</summary>
    Cancelled = 4,
    /// <summary>课程日期已过仍未审批。</summary>
    Expired = 5,
    /// <summary>强制换课已生效，对方老师仍可撤回。</summary>
    Forced = 6,
    /// <summary>强制换课被对方老师撤回，课表已恢复。</summary>
    Revoked = 7,
}

/// <summary>某班某日的一节课；快照字段只用于展示，服务端执行前会按最新课表复核。</summary>
public sealed class SwapSlot
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("className")]
    public string? ClassName { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("teacher")]
    public string? Teacher { get; set; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }
}

public sealed class CreateSwapRequest
{
    [JsonPropertyName("mode")]
    public SwapMode Mode { get; set; } = SwapMode.Exchange;

    /// <summary>要换走的课；替换模式下省略。</summary>
    [JsonPropertyName("source")]
    public SwapSlot? Source { get; set; }

    [JsonPropertyName("target")]
    public SwapSlot Target { get; set; } = new();

    /// <summary>替换模式下要换入的学科名，必须是申请人任教的学科。</summary>
    [JsonPropertyName("subjectName")]
    public string? SubjectName { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>强制换课：不经审批立即生效，需要 ForceScheduleSwap 权限。</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }
}

public sealed class SwapDecisionRequest
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class SwapRequestView
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>便于在聊天机器人中输入的短编号。</summary>
    [JsonPropertyName("shortId")]
    public string ShortId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public SwapMode Mode { get; set; }

    [JsonPropertyName("status")]
    public SwapRequestStatus Status { get; set; }

    [JsonPropertyName("forced")]
    public bool Forced { get; set; }

    [JsonPropertyName("requesterUserId")]
    public Guid RequesterUserId { get; set; }

    [JsonPropertyName("requesterName")]
    public string RequesterName { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public SwapSlot? Source { get; set; }

    [JsonPropertyName("target")]
    public SwapSlot Target { get; set; } = new();

    [JsonPropertyName("subjectName")]
    public string? SubjectName { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("approverNames")]
    public List<string> ApproverNames { get; set; } = [];

    [JsonPropertyName("decidedByName")]
    public string? DecidedByName { get; set; }

    [JsonPropertyName("decisionNote")]
    public string? DecisionNote { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("decidedAt")]
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>当前用户能否审批（通过/拒绝）。</summary>
    [JsonPropertyName("canDecide")]
    public bool CanDecide { get; set; }

    /// <summary>当前用户能否撤回这次强制换课。</summary>
    [JsonPropertyName("canRevoke")]
    public bool CanRevoke { get; set; }

    /// <summary>当前用户（申请人）能否撤销待审批申请。</summary>
    [JsonPropertyName("canCancel")]
    public bool CanCancel { get; set; }
}

/// <summary>换课页选择器数据：所有班级的七日课表（已叠加临时任课老师）与本人任教学科。</summary>
public sealed class SwapCatalog
{
    [JsonPropertyName("teacherName")]
    public string TeacherName { get; set; } = string.Empty;

    [JsonPropertyName("canForce")]
    public bool CanForce { get; set; }

    [JsonPropertyName("mySubjects")]
    public List<string> MySubjects { get; set; } = [];

    [JsonPropertyName("classes")]
    public List<SwapCatalogClass> Classes { get; set; } = [];
}

public sealed class SwapCatalogClass
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("subjects")]
    public List<string> Subjects { get; set; } = [];

    [JsonPropertyName("days")]
    public List<SwapCatalogDay> Days { get; set; } = [];
}

public sealed class SwapCatalogDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("courses")]
    public List<SwapCatalogCourse> Courses { get; set; } = [];
}

public sealed class SwapCatalogCourse
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("teacher")]
    public string? Teacher { get; set; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }

    /// <summary>该节课的老师是否为当前用户。</summary>
    [JsonPropertyName("mine")]
    public bool Mine { get; set; }
}

public static class UserNotificationKinds
{
    public const string SwapRequested = "swap_requested";
    public const string SwapApproved = "swap_approved";
    public const string SwapRejected = "swap_rejected";
    public const string SwapCancelled = "swap_cancelled";
    public const string SwapForced = "swap_forced";
    public const string SwapRevoked = "swap_revoked";
    /// <summary>发给相关班主任：本班课表因换课申请发生临时变化。</summary>
    public const string SwapHomeroomInfo = "swap_homeroom_info";
}

/// <summary>个人通知（user_notify 载荷与 /api/me/notifications 列表项）。</summary>
public sealed class UserNotificationView
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("swapRequestId")]
    public Guid? SwapRequestId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("readAt")]
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class MarkNotificationsReadRequest
{
    [JsonPropertyName("ids")]
    public List<Guid>? Ids { get; set; }

    [JsonPropertyName("all")]
    public bool All { get; set; }
}

public sealed class WebPushSubscriptionRequest
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("p256dh")]
    public string P256dh { get; set; } = string.Empty;

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = string.Empty;
}
