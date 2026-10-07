package com.remoteci.mobile.widget

import com.remoteci.mobile.data.ClassStateSnapshot
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.ScheduleBundle
import com.remoteci.mobile.data.ScheduleDay
import com.remoteci.mobile.data.Protocol
import java.time.LocalDate
import java.time.LocalTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class WidgetDataTest {
    @Test
    fun missingSnapshotAndScheduleProduceNoItems() {
        assertTrue(
            scheduleWidgetItems(
                snapshot = null,
                schedule = null,
                now = LocalTime.of(8, 0),
                today = LocalDate.of(2026, 10, 5),
            ).isEmpty(),
        )
    }

    @Test
    fun scheduleWithoutSnapshotStillShowsAvailableCourses() {
        val items = scheduleWidgetItems(
            snapshot = null,
            schedule = ScheduleBundle(
                days = listOf(
                    ScheduleDay(
                        date = "2026-10-05",
                        revision = "1",
                        enabled = true,
                        courses = listOf(
                            CourseEntry(0, "第1节", "math", "数学", "08:00", "08:40"),
                        ),
                    ),
                ),
            ),
            now = LocalTime.of(7, 45),
            today = LocalDate.of(2026, 10, 5),
        )

        assertEquals(listOf("数学"), items.map { it.subject })
        assertEquals("第1节", items.single().period)
    }

    @Test
    fun statusUsesNextLessonDuringBreak() {
        val content = statusWidgetContent(
            ClassStateSnapshot(
                currentState = Protocol.STATE_BREAKING,
                currentSubject = "数学",
                nextClassSubject = "语文",
                nextClassTimeLayoutItem = "第3节 10:10-10:50",
            ),
            now = LocalTime.of(10, 0),
        )

        assertEquals("语文", content.subject)
        assertEquals("10:10-10:50", content.timeRange)
        assertEquals("第3节", content.period)
    }

    @Test
    fun scheduleStartsAtNextCourseAndKeepsChronologicalOrder() {
        val schedule = ScheduleBundle(
            days = listOf(
                ScheduleDay(
                    date = "2026-10-05",
                    revision = "1",
                    enabled = true,
                    courses = listOf(
                        CourseEntry(0, "第1节", "math", "数学", "08:00", "08:40"),
                        CourseEntry(1, "第2节", "chinese", "语文", "09:00", "09:40"),
                    ),
                ),
            ),
        )
        val items = scheduleWidgetItems(
            snapshot = ClassStateSnapshot(scheduleDate = "2026-10-05"),
            schedule = schedule,
            now = LocalTime.of(8, 45),
            today = LocalDate.of(2026, 10, 5),
        )

        assertEquals(listOf("语文"), items.map { it.subject })
        assertTrue(items.first().timeRange == "09:00-09:40")
    }
}
