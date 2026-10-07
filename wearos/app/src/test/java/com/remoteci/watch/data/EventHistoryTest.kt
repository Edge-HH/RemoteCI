package com.remoteci.watch.data

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class EventHistoryTest {
    @Test
    fun `event history keeps newest ids in insertion order`() {
        assertEquals(listOf("b", "c"), appendEventId(listOf("a", "b"), "c", maxIds = 2))
        assertNull(appendEventId(listOf("a", "b"), "a", maxIds = 2))
    }
}
