package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ManagementLogicTest {
    @Test
    fun `no management items without user or permission`() {
        assertTrue(visibleManagementItems(null).isEmpty())
        val student = UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_ACCESS_WEB_UI)
        assertTrue(visibleManagementItems(student).isEmpty())
    }

    @Test
    fun `manage users permission unlocks people management items`() {
        val manager = UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_USERS)
        assertEquals(ManagementItem.entries.toList(), visibleManagementItems(manager))
    }
}
