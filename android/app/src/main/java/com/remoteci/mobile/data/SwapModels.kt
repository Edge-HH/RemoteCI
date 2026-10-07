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

    const val KIND_REQUESTED = "swap_requested"
    const val KIND_APPROVED = "swap_approved"
    const val KIND_REJECTED = "swap_rejected"
    const val KIND_CANCELLED = "swap_cancelled"
    const val KIND_FORCED = "swap_forced"
    const val KIND_REVOKED = "swap_revoked"
    const val KIND_HOMEROOM_INFO = "swap_homeroom_info"

    const val FORCE_WARNING = "仅在需要紧急换课时使用，请提前与对方沟通并达成一致。"

    fun statusText(status: Int): String = when (status) {
        STATUS_PENDING -> "待审批"
        STATUS_APPROVED -> "已通过"
        STATUS_REJECTED -> "已拒绝"
        STATUS_CANCELLED -> "已撤销"
        STATUS_EXPIRED -> "已过期"
        STATUS_FORCED -> "已强制换课"
        STATUS_REVOKED -> "已撤回"
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
)

@Serializable
data class SwapDecisionRequest(val note: String? = null)

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
)

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
