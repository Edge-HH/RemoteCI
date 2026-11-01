package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ControlLogicTest {
    private val allCaps = ControlItem.entries.map { it.capability }.toSet()
    private val allPermissions = ControlItem.entries.fold(0) { acc, item -> acc or item.permission }

    @Test
    fun `all items are visible with every permission and capability`() {
        assertEquals(ControlItem.entries.toList(), visibleControlItems(allPermissions, refined = true, capabilities = allCaps))
    }

    @Test
    fun `items need both permission and plugin capability`() {
        val items = visibleControlItems(
            Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_CAPTURE_SCREEN,
            refined = true,
            capabilities = setOf(Protocol.CAP_NOTIFICATION_SEND),
        )
        assertEquals(listOf(ControlItem.Notify), items)
    }

    @Test
    fun `legacy servers grant clear notifications through send notifications`() {
        val items = visibleControlItems(Protocol.PERMISSION_SEND_NOTIFICATIONS, refined = false, capabilities = allCaps)
        assertEquals(listOf(ControlItem.Notify, ControlItem.ClearNotifications), items)
    }

    @Test
    fun `power control is never a one tap tile or today shortcut`() {
        assertFalse(ControlItem.Power.quick)
        assertFalse(ControlItem.Power.todayShortcut)
    }

    @Test
    fun `today shortcuts fit in two rows of tiles`() {
        val shortcuts = ControlItem.entries.filter { it.todayShortcut }
        assertTrue(shortcuts.size <= 4)
    }
}
