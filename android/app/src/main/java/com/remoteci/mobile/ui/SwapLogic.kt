package com.remoteci.mobile.ui

import com.remoteci.mobile.data.AdminApiException
import com.remoteci.mobile.data.Swap
import com.remoteci.mobile.data.SwapCatalog
import com.remoteci.mobile.data.SwapConflict
import com.remoteci.mobile.data.SwapCatalogCourse
import com.remoteci.mobile.data.SwapRequestView
import com.remoteci.mobile.data.SwapSlot
import java.time.LocalDate

/** 底栏可见的板块：换课页只在账号拥有“老师主动换课”权限时出现；管理页只在至少有一项管理权限时出现。 */
/** 课表页的分段：我的日程、班级课表，以及有“老师主动换课”权限时的换课申请。 */
internal enum class ScheduleSegment { Mine, Class, SwapRequests }

internal fun visibleScheduleSegments(canRequestSwap: Boolean): List<ScheduleSegment> =
    ScheduleSegment.entries.filter { it != ScheduleSegment.SwapRequests || canRequestSwap }

/** 换课页里选中的一节课。 */
internal data class SwapPick(val classId: String, val date: String, val index: Int)

internal fun SwapCatalog.course(pick: SwapPick?): SwapCatalogCourse? = pick?.let { p ->
    classes.firstOrNull { it.classId == p.classId }?.days?.firstOrNull { it.date == p.date }
        ?.courses?.firstOrNull { it.index == p.index }
}

/** 默认的“要换走的课”：第一节属于自己的课；没有自己的课时为 null。 */
internal fun SwapCatalog.firstOwnPick(): SwapPick? {
    for (cls in classes) for (day in cls.days) for (course in day.courses)
        if (course.mine) return SwapPick(cls.classId, day.date, course.index)
    return null
}

/**
 * 提交前的本地校验，与服务端规则一致（服务端仍会复核）：返回错误提示，通过时返回 null。
 * 互换模式两节课至少一节是自己的课，替换模式必须选择自己任教的学科。
 */
internal fun validateSwap(
    catalog: SwapCatalog,
    exchange: Boolean,
    source: SwapPick?,
    target: SwapPick?,
    subject: String?,
    reason: String,
): String? {
    val targetCourse = catalog.course(target) ?: return "请选择目标课"
    if (reason.isBlank()) return "请填写换课理由"
    if (reason.trim().length > 200) return "换课理由不能超过 200 个字"
    if (exchange) {
        val sourceCourse = catalog.course(source) ?: return "请选择要换走的课"
        if (source == target) return "两节课不能是同一节"
        if (!sourceCourse.mine && !targetCourse.mine) return "两节课中至少要有一节是你自己的课"
    } else {
        if (subject.isNullOrBlank() || subject !in catalog.mySubjects) return "请选择你任教的学科"
        if (targetCourse.mine && targetCourse.subject == subject) return "目标课已经是你的这门课"
    }
    return null
}

private val weekdays = "一二三四五六日"

internal fun swapDateLabel(date: String): String = runCatching {
    val value = LocalDate.parse(date)
    "${value.monthValue}月${value.dayOfMonth}日 周${weekdays[value.dayOfWeek.value - 1]}"
}.getOrDefault(date)

internal fun swapCourseLabel(course: SwapCatalogCourse): String {
    val teacher = course.teacher?.takeIf { it.isNotBlank() }?.let { " · $it" }.orEmpty()
    return "${course.label} ${course.subject}$teacher${if (course.mine) "（我的课）" else ""}"
}

internal fun swapSlotText(slot: SwapSlot): String {
    val teacher = slot.teacher?.takeIf { it.isNotBlank() }?.let { " · $it" }.orEmpty()
    return "${slot.className.orEmpty()} ${swapDateLabel(slot.date)} ${slot.label.orEmpty()} ${slot.subject.orEmpty()}$teacher".trim()
}

/** 需要确认的冲突（任课冲突、同一节课的其他待审批申请），每条一行；没有时返回 null。 */
internal fun conflictLines(conflicts: List<SwapConflict>): String? =
    conflicts.filter { it.requiresAcknowledgement }.takeIf { it.isNotEmpty() }?.joinToString("\n") { "· ${it.message}" }

/** 失败是否为需要确认的换课冲突（409 SWAP_CONFLICTS）：是则返回给用户确认的冲突说明，否则返回 null。 */
internal fun Throwable.swapConflictText(): String? =
    (this as? AdminApiException)?.takeIf { it.code == Swap.ERROR_CONFLICTS }?.let { conflictLines(it.conflicts) ?: it.message }

/** 换课卡片上的补充说明：送达状态、失效原因与确认过的冲突。 */
internal fun swapStatusNotes(item: SwapRequestView): List<String> = buildList {
    if (item.delivery == Swap.DELIVERY_PENDING) add("教室电脑暂未确认，上线后自动生效")
    if (item.status == Swap.STATUS_INVALIDATED && !item.invalidReason.isNullOrBlank()) add("已失效：${item.invalidReason}")
    if (item.acknowledgedConflicts.isNotEmpty()) add("已确认的冲突：" + item.acknowledgedConflicts.joinToString("；") { it.message })
}

/** 一条申请的课位摘要：互换显示“A ⇄ B”，替换显示“目标 → 学科”。 */
internal fun swapSummary(item: SwapRequestView): String =
    if (item.mode == Swap.MODE_EXCHANGE && item.source != null) "${swapSlotText(item.source)} ⇄ ${swapSlotText(item.target)}"
    else "${swapSlotText(item.target)} → ${item.subjectName.orEmpty()} · ${item.requesterName}"
