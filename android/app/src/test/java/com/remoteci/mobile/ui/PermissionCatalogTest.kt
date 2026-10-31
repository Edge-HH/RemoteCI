package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserListItem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PermissionCatalogTest {
    private val notify = PermissionCatalog.items.first { it.value == Protocol.PERMISSION_SEND_NOTIFICATIONS && !it.isManagement }
    private val clear = PermissionCatalog.items.first { it.value == Protocol.PERMISSION_CLEAR_NOTIFICATIONS && !it.isManagement }
    private val roles = PermissionCatalog.items.first { it.value == Protocol.MANAGEMENT_MANAGE_ROLES && it.isManagement }
    private val base = PermissionCatalog.items.first { it.isBase }

    @Test
    fun `catalog lists each permission once and hides teacher coming`() {
        val classBits = PermissionCatalog.items.filterNot { it.isManagement }.map { it.value }
        val managementBits = PermissionCatalog.items.filter { it.isManagement }.map { it.value }
        assertEquals(classBits.size, classBits.toSet().size)
        assertEquals(managementBits.size, managementBits.toSet().size)
        assertFalse(Protocol.PERMISSION_TEACHER_COMING in classBits)
        assertEquals(Protocol.MANAGEMENT_ALL, managementBits.fold(0) { acc, bit -> acc or bit })
        assertEquals(8, PermissionCatalog.groups.size)
    }

    @Test
    fun `base permission is always checked and cannot be removed`() {
        val selection = PermissionSelection(permissions = 0)
        assertTrue(selection.has(base))
        assertEquals(selection, selection.with(base, false))
        assertTrue(PermissionSelection.ofRole(0, 0).permissions and Protocol.PERMISSION_VIEW_CURRENT != 0)
    }

    @Test
    fun `selection toggles class and management bits separately`() {
        val selection = PermissionSelection().with(clear, true).with(roles, true)
        assertTrue(selection.has(clear))
        assertFalse(selection.has(notify))
        assertEquals(Protocol.MANAGEMENT_MANAGE_ROLES, selection.management)
        assertFalse(selection.with(roles, false).has(roles))
    }

    @Test
    fun `group toggle checks every item of the group`() {
        val group = PermissionCatalog.groups.first { it.id == "messages" }
        val all = PermissionSelection().withGroup(group, true)
        assertTrue(group.items.all(all::has))
        assertFalse(group.items.any(all.withGroup(group, false)::has))
    }

    @Test
    fun `adjustment labels compare with role defaults`() {
        val defaults = PermissionSelection.ofRole(Protocol.PERMISSION_SEND_NOTIFICATIONS, 0)
        val selection = defaults.with(notify, false).with(clear, true)
        assertEquals("已收回", selection.adjustmentLabel(notify, defaults))
        assertEquals("已新增", selection.adjustmentLabel(clear, defaults))
        assertNull(selection.adjustmentLabel(base, defaults))
        assertNull(selection.adjustmentLabel(clear, null))
        assertFalse(selection.matches(defaults))
        assertTrue(defaults.matches(defaults))
    }

    @Test
    fun `adjustment is the difference from role defaults`() {
        val defaults = PermissionSelection.ofRole(
            Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_POWER_CONTROL,
            Protocol.MANAGEMENT_VIEW_DEVICES,
        )
        val selected = PermissionSelection(
            Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_VOLUME_CONTROL,
            Protocol.MANAGEMENT_MANAGE_ROLES,
        )
        assertEquals(
            PermissionAdjustment(
                granted = Protocol.PERMISSION_VOLUME_CONTROL,
                revoked = Protocol.PERMISSION_POWER_CONTROL,
                grantedManagement = Protocol.MANAGEMENT_MANAGE_ROLES,
                revokedManagement = Protocol.MANAGEMENT_VIEW_DEVICES,
            ),
            PermissionAdjustment.between(defaults, selected),
        )
        assertEquals(PermissionAdjustment(0, 0, 0, 0), PermissionAdjustment.between(defaults, defaults))
    }

    @Test
    fun `adjustment never touches base bits and keeps hidden bits from the account`() {
        val defaults = PermissionSelection.ofRole(Protocol.PERMISSION_TEACHER_COMING, 0)
        // 勾选结果里没有“老师来了”，但它不在清单中，不能因此被收回；原先单独授予的也保留。
        val selected = PermissionSelection(Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_CHANGE_DISPLAY_NAME, 0)
        val adjustment = PermissionAdjustment.between(defaults, selected, existingGranted = 0, existingRevoked = 0)
        assertEquals(0, adjustment.revoked)
        assertEquals(0, adjustment.granted)

        val kept = PermissionAdjustment.between(
            PermissionSelection.ofRole(0, 0), selected,
            existingGranted = Protocol.PERMISSION_TEACHER_COMING or Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )
        assertEquals(Protocol.PERMISSION_TEACHER_COMING, kept.granted)
    }

    @Test
    fun `edit selection uses effective permissions and falls back to role defaults`() {
        val defaults = PermissionSelection.ofRole(Protocol.PERMISSION_SEND_NOTIFICATIONS, Protocol.MANAGEMENT_VIEW_DEVICES)
        val refined = UserListItem(
            effectivePermissions = Protocol.PERMISSION_VOLUME_CONTROL,
            effectiveManagementPermissions = 0,
        )
        assertEquals(
            PermissionSelection(Protocol.PERMISSION_VOLUME_CONTROL or Protocol.PERMISSION_VIEW_CURRENT, 0),
            userSelection(refined, defaults),
        )
        // 旧服务端：不下发有效系统管理权限，退回角色默认。
        assertEquals(defaults, userSelection(UserListItem(), defaults))
    }
}
