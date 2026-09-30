package com.remoteci.watch.ui

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ThemeTest {
    @Test
    fun `palette lookup returns matching scheme and falls back to default`() {
        assertEquals("blue", WatchPalette.fromId("blue").id)
        assertEquals("lavender", WatchPalette.fromId("unknown-id").id)
        assertEquals("lavender", WatchPalette.fromId("").id)
    }

    @Test
    fun `built-in palettes have unique ids`() {
        val ids = WatchPalette.All.map { it.id }
        assertEquals(ids.size, ids.toSet().size)
        assertTrue(ids.isNotEmpty())
    }

    @Test
    fun `built-in palettes have non-blank unique labels`() {
        val labels = WatchPalette.All.map { it.label }
        assertEquals(labels.size, labels.toSet().size)
        assertTrue(labels.all { it.isNotBlank() })
    }
}
