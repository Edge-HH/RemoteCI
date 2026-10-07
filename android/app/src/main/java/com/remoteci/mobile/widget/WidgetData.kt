package com.remoteci.mobile.widget

import android.content.Context
import android.os.Bundle
import com.remoteci.mobile.data.ClassStateSnapshot
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyNextCourseResponse
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.ScheduleBundle
import com.remoteci.mobile.data.SnapshotStore
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/** 小组件展示所需的纯数据，避免 RemoteViews 直接依赖 Compose 状态。 */
data class WidgetSnapshot(
    val snapshot: ClassStateSnapshot?,
    val schedule: ScheduleBundle?,
    val personalNext: MyNextCourseResponse?,
)

data class StatusWidgetContent(
    val stateTitle: String,
    val subject: String,
    val period: String,
    val room: String,
    val timeRange: String,
    val nextLesson: String,
    val progress: Int,
)

data class ScheduleWidgetItem(
    val period: String,
    val subject: String,
    val timeRange: String,
)

object WidgetDataStore {
    fun load(context: Context): WidgetSnapshot = SnapshotStore(context.applicationContext).let {
        WidgetSnapshot(it.load(), it.loadSchedule(), it.loadPersonalNext())
    }
}

/**
 * 小组件尺寸单位是 dp。系统桌面与 HyperOS 都会在 onAppWidgetOptionsChanged 中提供实际尺寸，
 * 按高度决定课表行数，保证 2x2 至少显示下一节课，放大后逐步增加课程。
 */
fun scheduleItemCount(options: Bundle): Int {
    val width = options.getInt("appWidgetWidth", 0)
    val height = options.getInt("appWidgetHeight", 0)
    val byHeight = if (height <= 0) 1 else maxOf(1, (height - 68) / 42)
    val byWidth = when {
        width in 1..159 -> 1
        width in 160..239 -> 3
        width >= 240 -> Int.MAX_VALUE
        else -> 3
    }
    return maxOf(1, minOf(byHeight, byWidth))
}

fun statusWidgetContent(
    snapshot: ClassStateSnapshot?,
    personalNext: MyNextCourseResponse? = null,
    now: LocalTime = widgetLocalNow(snapshot),
): StatusWidgetContent {
    val personalCurrent = personalNext?.current
    val personalUpcoming = personalNext?.next
    if (personalNext != null) {
        val slot = personalCurrent ?: personalUpcoming
        val range = slot?.course?.let { listOfNotNull(it.startTime, it.endTime).joinToString("-") }.orEmpty()
        val teacherItem = range.ifBlank {
            listOfNotNull(slot?.startsAt?.let(::parseIsoTime), slot?.endsAt?.let(::parseIsoTime))
                .joinToString("-")
        }
        return StatusWidgetContent(
            stateTitle = when {
                personalCurrent != null -> "上课"
                personalUpcoming != null -> "即将上课"
                else -> "放学"
            },
            subject = slot?.course?.subject?.ifBlank { "暂无课程" } ?: "暂无课程",
            period = slot?.course?.label?.ifBlank { "下一节" } ?: "下一节",
            room = slot?.className.orEmpty(),
            timeRange = teacherItem.ifBlank { "—" },
            nextLesson = personalUpcoming?.course?.subject?.ifBlank { "暂无下一节" } ?: "暂无下一节",
            progress = personalCurrent?.let { progressFromIso(it.startsAt, it.endsAt) } ?: 0,
        )
    }
    val currentRange = snapshot?.currentTimeLayoutItem.orEmpty()
    val nextRange = snapshot?.nextClassTimeLayoutItem.orEmpty()
    val isNext = snapshot?.currentState in setOf(Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS)
    val subject = (if (isNext) snapshot?.nextClassSubject else snapshot?.currentSubject)
        ?.trim()?.takeIf(String::isNotEmpty) ?: if (isNext) "暂无下一节" else "暂无课程"
    val item = if (isNext) nextRange else currentRange
    val period = periodFrom(item) ?: if (isNext) "下一节" else "当前课"
    val range = rangeFrom(item).ifBlank { "—" }
    val progressItem = currentRange.takeIf { rangeFrom(it).isNotBlank() } ?: item
    val room = item.substringAfter(' ', missingDelimiterValue = "")
        .removePrefix(range).trim().ifBlank { snapshot?.classPlanName.orEmpty() }
    val stateTitle = when (snapshot?.currentState) {
        Protocol.STATE_CLASS -> "上课"
        Protocol.STATE_BREAKING -> "课间"
        Protocol.STATE_PREPARE_CLASS -> "即将上课"
        Protocol.STATE_AFTER_SCHOOL -> "放学"
        else -> "待机"
    }
    return StatusWidgetContent(
        stateTitle = stateTitle,
        subject = subject,
        period = period,
        room = room,
        timeRange = range,
        nextLesson = snapshot?.nextClassSubject?.trim().orEmpty().ifBlank { "暂无下一节" },
        progress = progressFrom(progressItem, now),
    )
}

fun scheduleWidgetItems(
    snapshot: ClassStateSnapshot?,
    schedule: ScheduleBundle?,
    now: LocalTime = widgetLocalNow(snapshot),
    today: LocalDate = snapshot?.scheduleDate?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
        ?: snapshot?.timeZoneOffsetMinutes?.let { offset ->
            runCatching { LocalDate.now(ZoneOffset.ofTotalSeconds(offset * 60)) }.getOrNull()
        }
        ?: LocalDate.now(),
): List<ScheduleWidgetItem> {
    val days = schedule?.days.orEmpty().sortedBy { it.date }
    val day = days.firstOrNull { it.date == today.toString() }
        ?: days.firstOrNull { it.date >= today.toString() }
    val courses = day?.courses.orEmpty().filter { it.enabled }.sortedBy { it.index }
    // “下一节”必须排除正在上的课程；没有当天后续课程时继续找下一教学日。
    val upcoming = courses.filter { course ->
        course.startTime?.let(::parseTime)?.let { it.isAfter(now) } ?: true
    }
    val source = if (upcoming.isNotEmpty()) {
        upcoming
    } else {
        days.asSequence()
            .filter { it.date > today.toString() }
            .map { nextDay -> nextDay.courses.filter { it.enabled }.sortedBy { it.index } }
            .firstOrNull { it.isNotEmpty() }
            .orEmpty()
    }
    if (source.isNotEmpty()) return source.map(::scheduleItem)
    if (snapshot == null) return emptyList()
    val fallback = snapshot.nextClassSubject?.trim().orEmpty()
    if (fallback.isBlank()) return emptyList()
    return listOf(
        ScheduleWidgetItem(
            period = periodFrom(snapshot.nextClassTimeLayoutItem) ?: "下一节",
            subject = fallback,
            timeRange = rangeFrom(snapshot.nextClassTimeLayoutItem).ifBlank { "—" },
        ),
    )
}

fun scheduleItemCountFor(options: Bundle, available: Int): Int = minOf(scheduleItemCount(options), available)

private fun scheduleItem(course: CourseEntry): ScheduleWidgetItem = ScheduleWidgetItem(
    period = course.label.ifBlank { "第${course.index + 1}节" },
    subject = course.subject.ifBlank { "未命名课程" },
    timeRange = listOfNotNull(course.startTime, course.endTime).joinToString("-").ifBlank { "—" },
)

private val timeRangeRegex = Regex("(\\d{1,2}:\\d{2})\\s*[-–—~至]\\s*(\\d{1,2}:\\d{2})")
private val periodRegex = Regex("第[一二三四五六七八九十\\d]+节")

private fun rangeFrom(value: String?): String = timeRangeRegex.find(value.orEmpty())?.let {
    "${it.groupValues[1]}-${it.groupValues[2]}"
}.orEmpty()

private fun periodFrom(value: String?): String? = periodRegex.find(value.orEmpty())?.value

private fun parseTime(value: String): LocalTime? = runCatching {
    LocalTime.parse(value, DateTimeFormatter.ofPattern("H:mm"))
}.getOrNull()

private fun parseIsoTime(value: String): String = runCatching {
    java.time.OffsetDateTime.parse(value).toLocalTime().toString().take(5)
}.getOrDefault("")

private fun progressFromIso(startsAt: String, endsAt: String): Int = runCatching {
    val start = java.time.OffsetDateTime.parse(startsAt).toInstant()
    val end = java.time.OffsetDateTime.parse(endsAt).toInstant()
    val total = java.time.Duration.between(start, end).seconds
    if (total <= 0) return@runCatching 0
    java.time.Duration.between(start, java.time.Instant.now()).seconds
        .times(100).div(total).toInt().coerceIn(0, 100)
}.getOrDefault(0)

private fun progressFrom(value: String?, now: LocalTime): Int {
    val match = timeRangeRegex.find(value.orEmpty()) ?: return 0
    val start = parseTime(match.groupValues[1]) ?: return 0
    val end = parseTime(match.groupValues[2]) ?: return 0
    val total = java.time.Duration.between(start, end).seconds
    if (total <= 0) return 0
    return (java.time.Duration.between(start, now).seconds * 100 / total).toInt().coerceIn(0, 100)
}

private fun widgetLocalNow(snapshot: ClassStateSnapshot?): LocalTime = snapshot?.timeZoneOffsetMinutes
    ?.let { offset -> runCatching { LocalTime.now(ZoneOffset.ofTotalSeconds(offset * 60)) }.getOrNull() }
    ?: LocalTime.now()
