package com.remoteci.mobile.calendar

import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyScheduleResponse
import com.remoteci.mobile.data.ScheduleBundle
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset

internal data class CalendarEvent(
    val key: String,
    val title: String,
    val description: String,
    val location: String,
    val startsAt: Long,
    val endsAt: Long,
    val timeZone: String,
)

/** 用日期、班级和节次标识课程；科目和时间变化时仍更新同一事件。 */
internal fun calendarEvents(
    schedule: MyScheduleResponse,
    now: Instant = Instant.now(),
    deviceZone: ZoneId = ZoneId.systemDefault(),
): List<CalendarEvent> = buildList {
    schedule.days.forEach { day ->
        val date = LocalDate.parse(day.date)
        day.items.forEach { item ->
            val zone = item.timeZoneOffsetMinutes?.let {
                require(it in -840..840) { "课程时区无效，未修改系统日历" }
                ZoneOffset.ofTotalSeconds(it * 60)
            } ?: deviceZone
            item.courses.filter { it.enabled }.forEach { course ->
                val window = courseWindow(date, course, zone)
                if (window.endsAt > now) add(CalendarEvent(
                    // 自定义课程每周重复，按课程与日期区分每一次。
                    key = course.personalCourseId?.let { "personal/$it/${day.date}" } ?: "${day.date}/${item.classId}/${course.index}",
                    title = "${course.subject} · ${item.className}",
                    description = listOfNotNull(
                        "RemoteCI 我的日程 · ${course.label}", course.teacher,
                        "按档案推算，可能与实际课表不同".takeIf { item.source == "profile" },
                    ).joinToString("\n"),
                    location = item.className,
                    startsAt = window.startsAt.toEpochMilli(), endsAt = window.endsAt.toEpochMilli(),
                    timeZone = window.timeZone,
                ))
            }
        }
    }
}.also(::requireUniqueKeys)

/**
 * 班级课表日历固定绑定一个班级，键中带班级 ID：改绑其他班级时旧班级的未来课程被删除，已结束的课程保留。
 * 课表包不含时区，按本机时区换算，与网页端没有状态快照时的推算一致。
 */
internal fun classCalendarEvents(
    bundle: ScheduleBundle,
    classId: String,
    className: String,
    now: Instant = Instant.now(),
    deviceZone: ZoneId = ZoneId.systemDefault(),
): List<CalendarEvent> = buildList {
    bundle.days.filter { it.enabled }.forEach { day ->
        val date = LocalDate.parse(day.date)
        day.courses.filter { it.enabled }.forEach { course ->
            val window = courseWindow(date, course, deviceZone)
            if (window.endsAt > now) add(CalendarEvent(
                key = "class/$classId/${day.date}/${course.index}",
                title = course.subject,
                description = listOfNotNull(
                    "RemoteCI 班级课表 · $className · ${course.label}", course.teacher,
                    "按档案推算，可能与实际课表不同".takeIf { bundle.fromProfile },
                ).joinToString("\n"),
                location = className,
                startsAt = window.startsAt.toEpochMilli(), endsAt = window.endsAt.toEpochMilli(),
                timeZone = window.timeZone,
            ))
        }
    }
}.also(::requireUniqueKeys)

private data class CourseWindow(val startsAt: Instant, val endsAt: Instant, val timeZone: String)

private fun courseWindow(date: LocalDate, course: CourseEntry, zone: ZoneId): CourseWindow {
    val absoluteStart = course.startsAt?.let(OffsetDateTime::parse)
    val absoluteEnd = course.endsAt?.let(OffsetDateTime::parse)
    val startsAt: Instant
    val endsAt: Instant
    if (absoluteStart != null || absoluteEnd != null) {
        startsAt = requireNotNull(absoluteStart).toInstant()
        endsAt = requireNotNull(absoluteEnd).toInstant()
    } else {
        val start = LocalTime.parse(requireNotNull(course.startTime) { "课程缺少开始时间，未修改系统日历" })
        val end = LocalTime.parse(requireNotNull(course.endTime) { "课程缺少结束时间，未修改系统日历" })
        require(end != start) { "课程起止时间相同，未修改系统日历" }
        startsAt = date.atTime(start).atZone(zone).toInstant()
        endsAt = (if (end < start) date.plusDays(1) else date).atTime(end).atZone(zone).toInstant()
    }
    require(endsAt > startsAt) { "课程结束时间无效，未修改系统日历" }
    return CourseWindow(startsAt, endsAt, when (val eventZone = absoluteStart?.offset ?: zone) {
        ZoneOffset.UTC -> "UTC"
        is ZoneOffset -> "GMT${eventZone.id}"
        else -> eventZone.id
    })
}

private fun requireUniqueKeys(events: List<CalendarEvent>) =
    require(events.map { it.key }.distinct().size == events.size) { "日程包含重复节次，未修改系统日历" }

internal data class StoredCalendarEvent(val id: Long, val event: CalendarEvent)

internal data class CalendarChanges(
    val inserts: List<CalendarEvent>,
    val updates: List<Pair<Long, CalendarEvent>>,
    val deletes: List<Long>,
)

internal fun calendarChanges(existing: List<StoredCalendarEvent>, desired: List<CalendarEvent>, now: Long): CalendarChanges {
    val byKey = existing.groupBy { it.event.key }
    val desiredKeys = desired.map { it.key }.toSet()
    return CalendarChanges(
        inserts = desired.filter { byKey[it.key].isNullOrEmpty() },
        updates = desired.mapNotNull { event ->
            byKey[event.key]?.firstOrNull()?.takeIf { it.event != event }?.let { it.id to event }
        },
        deletes = existing.filter {
            it.event.endsAt > now && (it.event.key !in desiredKeys || byKey[it.event.key]?.first()?.id != it.id)
        }.map { it.id },
    )
}
