package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ClassStateSnapshot
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyCourseSlot
import com.remoteci.mobile.data.MyNextCourseResponse
import com.remoteci.mobile.data.Protocol
import java.time.OffsetDateTime
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ScheduleLogicTest {
    @Test
    fun `sync time is rendered in the device time zone`() {
        assertEquals(
            "10:12",
            formatLocalSyncTime("2026-10-04T02:12:00+00:00", ZoneId.of("Asia/Shanghai")),
        )
    }

    @Test
    fun `invalid sync timestamp does not break the home status`() {
        assertNull(formatLocalSyncTime("not-a-timestamp", ZoneId.of("Asia/Shanghai")))
    }

    @Test
    fun `stage end comes from the time layout item on the plugin schedule date`() {
        val snapshot = ClassStateSnapshot(
            scheduleDate = "2026-10-05",
            currentState = Protocol.STATE_BREAKING,
            currentTimeLayoutItem = "9:30-10:05 课间休息",
            timeZoneOffsetMinutes = 480,
            onBreakingLeftTime = "00:00:00",
        )
        assertEquals(OffsetDateTime.parse("2026-10-05T10:05:00+08:00"), snapshotStageEnd(snapshot))
    }

    @Test
    fun `next class equal to the current lesson is dropped`() {
        val snapshot = ClassStateSnapshot(
            currentState = Protocol.STATE_CLASS,
            currentSubject = "体育",
            currentTimeLayoutItem = "10:05-10:45 体育",
            nextClassSubject = "体育",
            nextClassTimeLayoutItem = "10:05-10:45 体育",
        )
        assertNull(nextClassSubjectAfterCurrent(snapshot))
    }

    @Test
    fun `next class right after the current stage is kept`() {
        val snapshot = ClassStateSnapshot(
            currentState = Protocol.STATE_BREAKING,
            currentTimeLayoutItem = "9:30-10:05 课间休息",
            nextClassSubject = "体育",
            nextClassTimeLayoutItem = "10:05-10:45 体育",
        )
        assertEquals("体育", nextClassSubjectAfterCurrent(snapshot))
    }

    @Test
    fun `prepare before the first lesson counts down to the lesson start`() {
        val snapshot = ClassStateSnapshot(
            scheduleDate = "2026-10-05",
            currentState = Protocol.STATE_PREPARE_CLASS,
            nextClassTimeLayoutItem = "08:00-08:40 数学",
            timeZoneOffsetMinutes = 480,
        )
        assertEquals(OffsetDateTime.parse("2026-10-05T08:00:00+08:00"), snapshotStageEnd(snapshot))
    }

    @Test
    fun `stage end is absent without a parsable time range`() {
        assertNull(snapshotStageEnd(ClassStateSnapshot(scheduleDate = "2026-10-05", currentTimeLayoutItem = "课间休息")))
        assertNull(snapshotStageEnd(ClassStateSnapshot(scheduleDate = null, currentTimeLayoutItem = "09:30-10:05")))
    }

    private fun slot(subject: String, start: String, end: String) = MyCourseSlot(
        date = start.take(10),
        className = "高一1班",
        course = CourseEntry(index = 0, label = "第 1 节", subjectId = "", subject = subject),
        startsAt = start,
        endsAt = end,
    )

    @Test
    fun `teacher next lesson on a later day is after school not upcoming`() {
        // 周五晚上，下一节在下周一：不能整个周末显示“即将上课”。
        val monday = slot("语文", "2026-10-19T08:00:00+08:00", "2026-10-19T08:40:00+08:00")
        val state = teacherCourseState(MyNextCourseResponse(next = monday), OffsetDateTime.parse("2026-10-16T20:00:00+08:00"))
        assertNull(state.current)
        assertNull(state.laterToday)
        assertNull(state.upcoming)
        assertEquals(monday, state.next)
        assertEquals("放学", state.title)
        assertEquals("10-19 周一", teacherSlotDayLabel(monday, OffsetDateTime.parse("2026-10-16T20:00:00+08:00")))
        assertEquals("明天", teacherSlotDayLabel(monday, OffsetDateTime.parse("2026-10-18T20:00:00+08:00")))
    }

    @Test
    fun `teacher is upcoming only shortly before a lesson later today`() {
        val math = slot("数学", "2026-10-16T14:00:00+08:00", "2026-10-16T14:40:00+08:00")
        val response = MyNextCourseResponse(next = math)

        val morning = teacherCourseState(response, OffsetDateTime.parse("2026-10-16T09:00:00+08:00"))
        assertEquals(math, morning.laterToday)
        assertNull(morning.upcoming)
        assertEquals("空闲", morning.title)

        val soon = teacherCourseState(response, OffsetDateTime.parse("2026-10-16T13:52:00+08:00"))
        assertEquals(math, soon.upcoming)
        assertEquals("即将上课", soon.title)
        assertNull(teacherSlotDayLabel(math, OffsetDateTime.parse("2026-10-16T13:52:00+08:00")))
    }

    @Test
    fun `teacher state uses the classroom date even when the phone is in another zone`() {
        // 课程按教室 +08:00 的日期判断“今天”；手机时区是 UTC 时仍是同一天的课。
        val math = slot("数学", "2026-10-17T07:05:00+08:00", "2026-10-17T07:45:00+08:00")
        val state = teacherCourseState(MyNextCourseResponse(next = math), OffsetDateTime.parse("2026-10-16T23:00:00Z"))
        assertEquals(math, state.upcoming)
    }

    @Test
    fun `stale personal schedule is corrected by the current time`() {
        val first = slot("语文", "2026-10-16T08:00:00+08:00", "2026-10-16T08:40:00+08:00")
        val second = slot("数学", "2026-10-16T08:50:00+08:00", "2026-10-16T09:30:00+08:00")
        // 服务端在 08:30 算出的结果，08:55 时还没刷新：语文已下课、数学已经开始。
        val state = teacherCourseState(MyNextCourseResponse(current = first, next = second), OffsetDateTime.parse("2026-10-16T08:55:00+08:00"))
        assertEquals(second, state.current)
        assertNull(state.next)
        assertEquals("上课", state.title)

        val afterAll = teacherCourseState(MyNextCourseResponse(current = second), OffsetDateTime.parse("2026-10-16T10:00:00+08:00"))
        assertNull(afterAll.current)
        assertEquals("放学", afterAll.title)
    }

    @Test
    fun `schedule tabs show a week while swaps can use every reported day`() {
        val today = java.time.LocalDate.of(2026, 10, 6)
        val bundle = com.remoteci.mobile.data.ScheduleBundle(
            days = (0L until 30L).map { com.remoteci.mobile.data.ScheduleDay(today.plusDays(it).toString(), "r$it", enabled = true) },
        )
        assertEquals(30, availableScheduleDays(bundle, afterSchool = false, today = today).size)
        val week = displayScheduleDays(bundle, afterSchool = true, today = today)
        assertEquals(7, week.size)
        assertEquals("2026-10-07", week.first().date)
    }
}
