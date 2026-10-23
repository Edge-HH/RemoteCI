package com.remoteci.mobile.calendar

import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyScheduleDay
import com.remoteci.mobile.data.MyScheduleItem
import com.remoteci.mobile.data.MyScheduleResponse
import com.remoteci.mobile.data.ScheduleBundle
import com.remoteci.mobile.data.ScheduleDay
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class CalendarEventsTest {
    private val now = Instant.parse("2026-10-17T00:00:00Z")
    private val course = CourseEntry(1, "第一节", "math", "数学", startTime = "09:00", endTime = "09:45")
    private fun schedule(courses: List<CourseEntry> = listOf(course), offset: Int? = 480) = MyScheduleResponse(
        days = listOf(MyScheduleDay("2026-10-17", listOf(MyScheduleItem("class-a", "一班", courses, timeZoneOffsetMinutes = offset)))),
    )
    private fun events(schedule: MyScheduleResponse = schedule()) = calendarEvents(schedule, now, ZoneId.of("America/New_York"))

    @Test fun `classroom offset determines reminder time even when phone is abroad`() {
        val event = events().single()
        assertEquals(Instant.parse("2026-10-17T01:00:00Z").toEpochMilli(), event.startsAt)
        assertEquals("GMT+08:00", event.timeZone)
    }

    @Test fun `old server falls back to device zone`() {
        val event = events(schedule(offset = null)).single()
        assertEquals(Instant.parse("2026-10-17T13:00:00Z").toEpochMilli(), event.startsAt)
    }

    @Test fun `course crossing midnight ends on the next date`() {
        val event = events(schedule(listOf(course.copy(startTime = "23:45", endTime = "00:30")))).single()
        assertEquals(45L * 60_000, event.endsAt - event.startsAt)
    }

    @Test fun `ended and disabled courses are not exported`() {
        assertTrue(events(schedule(listOf(course.copy(enabled = false)))).isEmpty())
        assertTrue(events(schedule(listOf(course.copy(startTime = "07:00", endTime = "07:45")))).isEmpty())
    }

    @Test fun `identical second import performs no event mutations`() {
        val event = events().single()
        assertEquals(CalendarChanges(emptyList(), emptyList(), emptyList()),
            calendarChanges(listOf(StoredCalendarEvent(12, event)), listOf(event), now.toEpochMilli()))
    }

    @Test fun `subject and time changes update the existing event`() {
        val original = events().single()
        val changed = events(schedule(listOf(course.copy(subject = "英语", startTime = "10:00")))).single()
        val result = calendarChanges(listOf(StoredCalendarEvent(12, original)), listOf(changed), now.toEpochMilli())
        assertTrue(result.inserts.isEmpty())
        assertEquals(listOf(12L to changed), result.updates)
    }

    @Test fun `cancellation removes future events and preserves history`() {
        val event = events().single()
        val history = event.copy(key = "past", startsAt = 0, endsAt = 1)
        val result = calendarChanges(listOf(StoredCalendarEvent(12, event), StoredCalendarEvent(13, history)), emptyList(), now.toEpochMilli())
        assertEquals(listOf(12L), result.deletes)
    }

    @Test fun `duplicate provider events are consolidated`() {
        val event = events().single()
        val result = calendarChanges(listOf(StoredCalendarEvent(12, event), StoredCalendarEvent(13, event)), listOf(event), now.toEpochMilli())
        assertEquals(listOf(13L), result.deletes)
        assertTrue(result.inserts.isEmpty())
    }

    @Test fun `invalid response is rejected before reconciliation`() {
        assertFailsWith<IllegalArgumentException> { events(schedule(listOf(course.copy(endTime = null)))) }
        assertFailsWith<IllegalArgumentException> { events(schedule(listOf(course, course))) }
        assertFailsWith<IllegalArgumentException> { events(schedule(offset = 1000)) }
    }

    @Test fun `weekly custom course gets one event per date and uses absolute time`() {
        fun custom(date: String, id: String) = course.copy(personalCourseId = id,
            startsAt = "${date}T09:00:00+03:00", endsAt = "${date}T09:45:00+03:00")
        val weekly = MyScheduleResponse(days = listOf("2026-10-17", "2026-10-24").map { date ->
            MyScheduleDay(date, listOf(MyScheduleItem("00000000-0000-0000-0000-000000000000", "自定义课程", listOf(custom(date, "a")), timeZoneOffsetMinutes = 180)))
        })
        val result = events(weekly)
        assertEquals(listOf("personal/a/2026-10-17", "personal/a/2026-10-24"), result.map { it.key })
        assertEquals(Instant.parse("2026-10-24T06:00:00Z").toEpochMilli(), result.last().startsAt)
        val renamed = events(MyScheduleResponse(days = listOf(weekly.days.first().copy(
            items = listOf(weekly.days.first().items.single().copy(courses = listOf(custom("2026-10-17", "a").copy(subject = "改名"))))))))
        assertEquals(result.first().key, renamed.single().key)
    }

    private fun bundle(courses: List<CourseEntry> = listOf(course), enabled: Boolean = true, source: String? = null) = ScheduleBundle(
        classId = "class-a", days = listOf(ScheduleDay("2026-10-17", "r1", enabled = enabled, courses = courses)), source = source,
    )
    private fun classEvents(bundle: ScheduleBundle = bundle(), classId: String = "class-a") =
        classCalendarEvents(bundle, classId, "一班", now, ZoneId.of("Asia/Shanghai"))

    @Test fun `class schedule uses device zone and class bound keys`() {
        val event = classEvents().single()
        assertEquals(Instant.parse("2026-10-17T01:00:00Z").toEpochMilli(), event.startsAt)
        assertEquals("class/class-a/2026-10-17/1", event.key)
        assertEquals("数学", event.title)
        assertEquals("一班", event.location)
    }

    @Test fun `class schedule skips disabled days and courses`() {
        assertTrue(classEvents(bundle(enabled = false)).isEmpty())
        assertTrue(classEvents(bundle(listOf(course.copy(enabled = false)))).isEmpty())
    }

    @Test fun `profile derived class schedule is labelled`() {
        assertTrue(classEvents(bundle(source = "profile")).single().description.contains("按档案推算"))
    }

    @Test fun `rebinding to another class removes the old class future events`() {
        val old = classEvents().single()
        val rebound = classEvents(classId = "class-b")
        val result = calendarChanges(listOf(StoredCalendarEvent(12, old)), rebound, now.toEpochMilli())
        assertEquals(listOf(12L), result.deletes)
        assertEquals(rebound, result.inserts)
    }
}
