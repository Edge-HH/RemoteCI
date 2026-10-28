package com.remoteci.mobile.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** 换课申请（老师主动换课）相关的 REST 模型，对应服务端 /api/swap-requests 与 /api/me/notifications。 */
object Swap {
    const val MODE_EXCHANGE = 1
    const val MODE_REPLACE = 2

    const val STATUS_PENDING = 1
    const val STATUS_APPROVED = 2
    const val STATUS_REJECTED = 3
    const val STATUS_CANCELLED = 4
    const val STATUS_EXPIRED = 5
    const val STATUS_FORCED = 6
    const val STATUS_REVOKED = 7
    const val STATUS_INVALIDATED = 8

    /** 已生效换课的送达状态：教室端尚未确认（离线等）/ 已生效。 */
    const val DELIVERY_PENDING = "pending"
    const val DELIVERY_APPLIED = "applied"

    /** 换课冲突提示；任课冲突与同一节课的其他待审批申请需要确认后才能提交或通过。 */
    const val CONFLICT_TEACHER_CLASH = "teacher_clash"
    const val CONFLICT_SLOT_PENDING = "slot_pending"
    const val CONFLICT_SLOT_ADJUSTED = "slot_adjusted"
    const val ERROR_CONFLICTS = "SWAP_CONFLICTS"

    const val KIND_REQUESTED = "swap_requested"
    const val KIND_APPROVED = "swap_approved"
    const val KIND_REJECTED = "swap_rejected"
    const val KIND_CANCELLED = "swap_cancelled"
    const val KIND_FORCED = "swap_forced"
    const val KIND_REVOKED = "swap_revoked"
    const val KIND_HOMEROOM_INFO = "swap_homeroom_info"
    const val KIND_INVALIDATED = "swap_invalidated"

    const val FORCE_WARNING = "仅在需要紧急换课时使用，请提前与对方沟通并达成一致。"

    fun statusText(status: Int): String = when (status) {
        STATUS_PENDING -> "待审批"
        STATUS_APPROVED -> "已通过"
        STATUS_REJECTED -> "已拒绝"
        STATUS_CANCELLED -> "已撤销"
        STATUS_EXPIRED -> "已过期"
        STATUS_FORCED -> "已强制换课"
        STATUS_REVOKED -> "已撤回"
        STATUS_INVALIDATED -> "已失效"
        else -> "未知"
    }
}

@Serializable
data class SwapSlot(
    @SerialName("classId") val classId: String,
    val date: String,
    val index: Int,
    @SerialName("className") val className: String? = null,
    val label: String? = null,
    val subject: String? = null,
    val teacher: String? = null,
    @SerialName("startTime") val startTime: String? = null,
    @SerialName("endTime") val endTime: String? = null,
)

@Serializable
data class CreateSwapRequest(
    val mode: Int,
    val source: SwapSlot? = null,
    val target: SwapSlot,
    @SerialName("subjectName") val subjectName: String? = null,
    val reason: String,
    val force: Boolean = false,
    /** 已看过冲突提示并确认继续；未确认时有需要确认的冲突会返回 409 SWAP_CONFLICTS。 */
    @SerialName("acknowledgeConflicts") val acknowledgeConflicts: Boolean = false,
)

@Serializable
data class SwapDecisionRequest(
    val note: String? = null,
    @SerialName("acknowledgeConflicts") val acknowledgeConflicts: Boolean = false,
)

/** 提交或审批换课前检查出的冲突。 */
@Serializable
data class SwapConflict(
    val kind: String = "",
    val message: String = "",
    @SerialName("requiresAcknowledgement") val requiresAcknowledgement: Boolean = false,
    @SerialName("className") val className: String? = null,
    val date: String? = null,
    val label: String? = null,
    val teacher: String? = null,
)

@Serializable
data class SwapRequestView(
    val id: String,
    @SerialName("shortId") val shortId: String = "",
    val mode: Int = Swap.MODE_EXCHANGE,
    val status: Int = Swap.STATUS_PENDING,
    val forced: Boolean = false,
    @SerialName("requesterName") val requesterName: String = "",
    val source: SwapSlot? = null,
    val target: SwapSlot,
    @SerialName("subjectName") val subjectName: String? = null,
    val reason: String = "",
    @SerialName("approverNames") val approverNames: List<String> = emptyList(),
    @SerialName("decidedByName") val decidedByName: String? = null,
    @SerialName("decisionNote") val decisionNote: String? = null,
    @SerialName("createdAt") val createdAt: String = "",
    @SerialName("canDecide") val canDecide: Boolean = false,
    @SerialName("canRevoke") val canRevoke: Boolean = false,
    @SerialName("canCancel") val canCancel: Boolean = false,
    /** 计入“待我处理”：可审批，或强制换课尚未看过；旧服务端没有该字段时按可审批/可撤回计。 */
    @SerialName("needsAction") val needsAction: Boolean? = null,
    /** 相关班级的班主任或系统管理员可撤回已生效的换课。 */
    @SerialName("canRevert") val canRevert: Boolean = false,
    /** 已生效换课的送达状态（pending / applied）；其他状态为 null。 */
    val delivery: String? = null,
    @SerialName("invalidReason") val invalidReason: String? = null,
    @SerialName("acknowledgedConflicts") val acknowledgedConflicts: List<SwapConflict> = emptyList(),
) {
    val actionRequired: Boolean get() = needsAction ?: (canDecide || canRevoke)
}

@Serializable
data class MarkSwapsSeenRequest(val ids: List<String>)

@Serializable
data class SwapCatalog(
    @SerialName("teacherName") val teacherName: String = "",
    @SerialName("canForce") val canForce: Boolean = false,
    @SerialName("mySubjects") val mySubjects: List<String> = emptyList(),
    val classes: List<SwapCatalogClass> = emptyList(),
)

@Serializable
data class SwapCatalogClass(
    @SerialName("classId") val classId: String,
    @SerialName("className") val className: String,
    val subjects: List<String> = emptyList(),
    /** 可选日期范围（今天起 30 天，含首尾）；旧服务端不下发，按 days 推算。 */
    @SerialName("minDate") val minDate: String = "",
    @SerialName("maxDate") val maxDate: String = "",
    /** 范围内自己有课的日期，用于默认选中“要换走的课”。 */
    @SerialName("myDates") val myDates: List<String> = emptyList(),
    /** 今天起 7 天的课程；更远的日期用 AdminApi.swapCatalogDay 按天读取。 */
    val days: List<SwapCatalogDay> = emptyList(),
)

@Serializable
data class SwapCatalogDay(val date: String, val courses: List<SwapCatalogCourse> = emptyList())

@Serializable
data class SwapCatalogCourse(
    val index: Int,
    val label: String = "",
    val subject: String = "",
    val teacher: String? = null,
    @SerialName("startTime") val startTime: String? = null,
    @SerialName("endTime") val endTime: String? = null,
    val mine: Boolean = false,
)

/** 个人通知：user_notify 载荷与 /api/me/notifications 列表项。 */
@Serializable
data class UserNotification(
    val id: String,
    val kind: String = "",
    val title: String = "",
    val body: String = "",
    @SerialName("swapRequestId") val swapRequestId: String? = null,
    @SerialName("createdAt") val createdAt: String = "",
    @SerialName("readAt") val readAt: String? = null,
)

@Serializable
data class MarkNotificationsReadRequest(val ids: List<String>? = null, val all: Boolean = false)
