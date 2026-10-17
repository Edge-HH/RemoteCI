package com.remoteci.mobile.calendar

import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyScheduleDay
import com.remoteci.mobile.data.MyScheduleItem
import com.remoteci.mobile.data.MyScheduleResponse
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

    @Test fun `custom course keeps its id after a date change and uses absolute time`() {
        fun custom(date: String, id: String) = course.copy(personalCourseId = id,
            startsAt = "${date}T09:00:00+03:00", endsAt = "${date}T09:45:00+03:00")
        val old = events(schedule(listOf(custom("2026-10-17", "a")))).single()
        val changed = events(schedule(listOf(custom("2026-10-18", "a"), custom("2026-10-18", "b"))))
        assertEquals(old.key, changed.first().key)
        assertEquals(Instant.parse("2026-10-18T06:00:00Z").toEpochMilli(), changed.first().startsAt)
        assertEquals(2, changed.map { it.key }.distinct().size)
    }
}
