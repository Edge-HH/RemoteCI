package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ClassStateSnapshot
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
}
