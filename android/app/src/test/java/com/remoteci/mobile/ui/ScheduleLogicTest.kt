package com.remoteci.mobile.ui

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
}
