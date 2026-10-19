package com.remoteci.watch.data

import java.time.OffsetDateTime

/** 自定义课程不属于任何班级，服务端以全零 GUID 作为其 classId。 */
internal const val PersonalCourseClassId = "00000000-0000-0000-0000-000000000000"

/** 下一节课在该时长内开始时提前切到其所在班级，与手机端一致。 */
internal const val AutoFollowLeadMs = 10 * 60_000L

/** 手表没有常驻的个人日程轮询：仅在开关打开时按该周期读取当前/下一节课并判定一次。 */
internal const val AutoFollowPollMs = 60_000L

/** 可开启自动跟随的账号：与服务端一致，只有内置老师与班主任角色按显示名绑定课表，且至少能访问两个班级。 */
internal fun canAutoFollowTeachingClass(user: UserProfile?, classes: List<ClassSummary>): Boolean =
    user?.hasPersonalSchedule == true && classes.size > 1

/**
 * 自动跟随的目标课程：正在上的课优先；没有当前课，或当前课是自定义课程、不属于可访问班级时，
 * 取 [AutoFollowLeadMs] 内开始的下一节。轮询结果可能滞后一个周期，已经结束的课一律不算。
 */
internal fun teachingClassTarget(next: MyNextCourseResponse?, classes: List<ClassSummary>, nowMs: Long): MyCourseSlot? {
    fun MyCourseSlot.followable(): Boolean =
        !classId.equals(PersonalCourseClassId, ignoreCase = true) &&
            classes.any { it.id.equals(classId, ignoreCase = true) } &&
            (courseEpochMillis(endsAt)?.let { it > nowMs } == true)
    next ?: return null
    next.current?.takeIf { it.followable() }?.let { return it }
    val upcoming = next.next?.takeIf { it.followable() } ?: return null
    val startsAt = courseEpochMillis(upcoming.startsAt) ?: return null
    return upcoming.takeIf { startsAt - nowMs <= AutoFollowLeadMs }
}

/** 手动选班的保持期：到当前课结束，没有当前课时到下一节结束；都没有（或都已结束）时不保持。 */
internal fun manualClassHoldUntil(next: MyNextCourseResponse?, nowMs: Long): Long =
    listOfNotNull(next?.current, next?.next)
        .firstNotNullOfOrNull { slot -> courseEpochMillis(slot.endsAt)?.takeIf { it > nowMs } }
        ?: 0L

/** 自动换班后的提示，如“已切换到 高二3班（第3节 数学）”。 */
internal fun autoClassSwitchNotice(className: String, slot: MyCourseSlot): String {
    val course = listOf(slot.course.label, slot.course.subject).filter(String::isNotBlank).joinToString(" ")
    return if (course.isEmpty()) "已切换到 $className" else "已切换到 $className（$course）"
}

private fun courseEpochMillis(value: String): Long? =
    runCatching { OffsetDateTime.parse(value).toInstant().toEpochMilli() }.getOrNull()
