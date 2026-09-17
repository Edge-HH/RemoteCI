package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ClassStateSnapshot
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.ScheduleBundle
import com.remoteci.mobile.data.ScheduleDay
import java.time.Duration
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneOffset

data class LessonChoice(
    val id: String,
    val index: Int,
    val periodLabel: String,
    val subject: String,
    val time: String,
    val commandValue: String,
    val enabled: Boolean,
)

internal fun availableScheduleDays(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): List<ScheduleDay> {
    val firstVisibleDate = if (afterSchool) today.plusDays(1) else today
    return bundle?.days.orEmpty()
        .filter { day -> parseScheduleDate(day.date)?.isBefore(firstVisibleDate) != true }
        .sortedBy { day -> parseScheduleDate(day.date) ?: LocalDate.MAX }
}

internal fun initialScheduleDate(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): String? = availableScheduleDays(bundle, afterSchool, today).firstOrNull()?.date

internal fun scheduleDateTitle(date: String, today: LocalDate = LocalDate.now()): String {
    val suffix = when (parseScheduleDate(date)) {
        today -> "-今天"
        today.plusDays(1) -> "-明天"
        else -> ""
    }
    return "${date.takeLast(5)}$suffix"
}

internal fun parseScheduleDate(date: String): LocalDate? = runCatching { LocalDate.parse(date) }.getOrNull()

internal fun buildLessonChoices(day: ScheduleDay?): List<LessonChoice> = day?.courses?.map { it.toChoice() }.orEmpty()

/** 保留当前课程映射测试，产品换课路径使用上面的 ScheduleDay 重载。 */
internal fun buildLessonChoices(snapshot: ClassStateSnapshot?): List<LessonChoice> = listOf(
    LessonChoice("current", 0, extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", snapshot?.currentSubject ?: "未加载课表", extractTimeRange(snapshot?.currentTimeLayoutItem), extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", !snapshot?.currentSubject.isNullOrBlank()),
    LessonChoice("next", 1, extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", snapshot?.nextClassSubject ?: "暂无下一节", extractTimeRange(snapshot?.nextClassTimeLayoutItem), extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", !snapshot?.nextClassSubject.isNullOrBlank()),
)

internal fun CourseEntry.toChoice() = LessonChoice(
    id = index.toString(), index = index, periodLabel = label, subject = subject,
    time = listOfNotNull(startTime, endTime).joinToString("-"), commandValue = index.toString(), enabled = enabled,
)

internal val TimeRangeRegex = Regex("(\\d{1,2}:\\d{2})\\s*[-–—~至]\\s*(\\d{1,2}:\\d{2})")
internal val LessonPeriodRegex = Regex("第[一二三四五六七八九十\\d]+节")
internal fun extractPeriod(value: String?): String? = value?.let(LessonPeriodRegex::find)?.value
internal fun extractTimeRange(value: String?): String {
    val match = value?.let(TimeRangeRegex::find) ?: return ""
    return "${match.groupValues[1]}-${match.groupValues[2]}"
}

/**
 * 把当前时间段（如 "16:30-17:10 语文"）匹配到当天课表的课程零基索引，
 * 供主界面课程按钮“点击快速选中该课换课”预选源课使用。
 * 匹配不到（课表未同步、当前无课或时间段不一致）时返回 null。
 */
internal fun currentLessonIndex(day: ScheduleDay?, value: String?): Int? {
    val range = extractTimeRange(value)
    if (range.isBlank() || day == null) return null
    val parts = range.split("-")
    if (parts.size != 2) return null
    val (start, end) = parts
    return day.courses.firstOrNull { it.enabled && it.startTime == start && it.endTime == end }?.index
}

internal data class HomeCourseContent(
    val subject: String,
    val timeLayoutItem: String?,
    val targetsNextLesson: Boolean,
    val isAvailable: Boolean,
)

/**
 * 决定主页主课程按钮代表当前课还是下一节课。
 * 下课/即将上课阶段没有正在进行的课程，因此按钮直接承载下一节课及其快速换课入口。
 */
internal fun homeCourseContent(snapshot: ClassStateSnapshot?): HomeCourseContent {
    val targetsNextLesson = snapshot?.currentState in setOf(
        Protocol.STATE_BREAKING,
        Protocol.STATE_PREPARE_CLASS,
    )
    val subject = if (targetsNextLesson) snapshot?.nextClassSubject else snapshot?.currentSubject
    val availableSubject = subject?.trim()?.takeIf(String::isNotEmpty)
    return if (targetsNextLesson) {
        HomeCourseContent(
            subject = availableSubject ?: "暂无下一节",
            timeLayoutItem = snapshot?.nextClassTimeLayoutItem,
            targetsNextLesson = true,
            isAvailable = availableSubject != null,
        )
    } else {
        HomeCourseContent(
            subject = availableSubject ?: "暂无课程",
            timeLayoutItem = snapshot?.currentTimeLayoutItem,
            targetsNextLesson = false,
            isAvailable = availableSubject != null,
        )
    }
}

/** 主按钮已经显示下一节课时，不再在页面底部重复显示“下一节课是”。 */
internal fun shouldShowNextLessonSummary(currentState: Int?): Boolean =
    currentState !in setOf(Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS)

/** 快速换课预选项必须与主页按钮当前显示的课程一致。 */
internal fun homeQuickSwapLessonIndex(day: ScheduleDay?, snapshot: ClassStateSnapshot?): Int? =
    currentLessonIndex(day, homeCourseContent(snapshot).timeLayoutItem)

/** “暂无课程”状态下的下一节课入口始终按下一时间段匹配，不能回退到空的当前时间段。 */
internal fun nextQuickSwapLessonIndex(day: ScheduleDay?, snapshot: ClassStateSnapshot?): Int? =
    currentLessonIndex(day, snapshot?.nextClassTimeLayoutItem)

/** 仅在截图所示的“当前无课、但有下一节课”状态展示下一节课描边操作入口。 */
internal fun shouldHighlightNextLessonAction(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.currentState == Protocol.STATE_NONE &&
        !homeCourseContent(snapshot).isAvailable &&
        !snapshot.nextClassSubject.isNullOrBlank()

/** 只有具有明确起止时间的课程阶段才显示环状进度，避免放学等开放状态产生伪进度。 */
internal fun shouldShowStateProgress(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.currentState in setOf(
        Protocol.STATE_CLASS,
        Protocol.STATE_PREPARE_CLASS,
        Protocol.STATE_BREAKING,
    ) && extractTimeRange(snapshot?.currentTimeLayoutItem).isNotBlank()

internal fun lessonProgress(value: String?, now: LocalTime): Float {
    val match = value?.let(TimeRangeRegex::find) ?: return 0f
    val start = runCatching { LocalTime.parse(match.groupValues[1]) }.getOrNull() ?: return 0f
    val end = runCatching { LocalTime.parse(match.groupValues[2]) }.getOrNull() ?: return 0f
    val total = Duration.between(start, end).seconds
    return if (total <= 0) 0f else (Duration.between(start, now).seconds.toFloat() / total).coerceIn(0f, 1f)
}

/**
 * 根据插件快照推算“插件本地当前时间”：
 * 以快照的 UTC 生成时间为基准，加上插件时区偏移和本机经过的真实时间，
 * 即使手表时区与插件不一致，课程进度也能按插件时间轴正确计算。
 *
 * @param generatedAt 快照的 UTC 生成时间（ISO-8601，如 "2026-08-12T08:46:55+00:00"）。
 * @param offsetMinutes 插件本地时区相对 UTC 的偏移分钟数；为 null 表示旧版插件无偏移信息。
 * @param baseElapsedMs 收到当前快照时本机 elapsedRealtime 毫秒数。
 * @param nowElapsedMs 当前本机 elapsedRealtime 毫秒数。
 */
internal fun pluginLocalNow(
    generatedAt: String?,
    offsetMinutes: Int?,
    baseElapsedMs: Long,
    nowElapsedMs: Long,
): LocalTime {
    val offset = offsetMinutes?.let { runCatching { ZoneOffset.ofTotalSeconds(it * 60) }.getOrNull() }
    if (offset == null) return LocalTime.now()
    val base = generatedAt?.let { raw ->
        runCatching { OffsetDateTime.parse(raw).toInstant().atOffset(offset).toLocalTime() }.getOrNull()
    } ?: LocalTime.now(offset)
    return base.plusNanos((nowElapsedMs - baseElapsedMs) * 1_000_000L)
}

/**
 * 推算插件侧“今天”的日期，与 [pluginLocalNow] 同源：以快照 UTC 生成时间加插件时区偏移为基准，
 * 再按本机流逝时间外推。手表与插件时区不一致时不会取错日期；缺时区信息退回表端日期。
 */
internal fun pluginToday(
    generatedAt: String?,
    offsetMinutes: Int?,
    baseElapsedMs: Long,
    nowElapsedMs: Long,
): LocalDate {
    val offset = offsetMinutes?.let { runCatching { ZoneOffset.ofTotalSeconds(it * 60) }.getOrNull() }
    if (offset == null) return LocalDate.now()
    val base = generatedAt?.let { raw ->
        runCatching { OffsetDateTime.parse(raw).toInstant().atOffset(offset).toLocalDateTime() }.getOrNull()
    } ?: LocalDateTime.now(offset)
    return base.plusNanos((nowElapsedMs - baseElapsedMs) * 1_000_000L).toLocalDate()
}
