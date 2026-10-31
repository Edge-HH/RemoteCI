package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
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
        assertEquals(
            listOf(ManagementItem.Users, ManagementItem.Roles, ManagementItem.VisitorAccess, ManagementItem.Pairing),
            visibleManagementItems(manager),
        )
    }

    @Test
    fun `global settings are only listed for system admins`() {
        val admin = UserProfile(role = Protocol.ROLE_ADMIN, permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_USERS)
        assertEquals(ManagementItem.entries.toList(), visibleManagementItems(admin))
    }

    @Test
    fun `old server keeps schedule settings admin only`() {
        val manager = UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_USERS)
        assertFalse(ManagementItem.ScheduleSettings in visibleManagementItems(manager))
        assertFalse(ManagementItem.ServerMaintenance in visibleManagementItems(manager))
    }

    @Test
    fun `refined server checks each management bit`() {
        val manager = UserProfile(
            permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_USERS,
            managementPermissions = 0,
        )
        assertEquals(listOf(ManagementItem.Users), visibleManagementItems(manager))

        val roleEditor = UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT, managementPermissions = Protocol.MANAGEMENT_MANAGE_ROLES)
        assertEquals(listOf(ManagementItem.Roles), visibleManagementItems(roleEditor))

        val operator = UserProfile(
            permissions = Protocol.PERMISSION_VIEW_CURRENT,
            managementPermissions = Protocol.MANAGEMENT_MANAGE_LOGIN_PAGE or Protocol.MANAGEMENT_MANAGE_PLUGIN_ACCESS,
        )
        assertEquals(
            listOf(ManagementItem.VisitorAccess, ManagementItem.Pairing, ManagementItem.ScheduleSettings),
            visibleManagementItems(operator),
        )
    }

    @Test
    fun `server maintenance stays admin only even with all management bits`() {
        val everything = UserProfile(permissions = Protocol.PERMISSION_ALL, managementPermissions = Protocol.MANAGEMENT_ALL)
        assertFalse(ManagementItem.ServerMaintenance in visibleManagementItems(everything))
        assertTrue(ManagementItem.ScheduleSettings in visibleManagementItems(everything))
    }
}
