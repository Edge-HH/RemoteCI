package com.remoteci.mobile.data

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RefinedPermissionsTest {
    @Test
    fun `old server falls back to the merged legacy bit`() {
        val legacy = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_POWER_CONTROL
        assertTrue(Protocol.hasPermission(legacy, Protocol.PERMISSION_CLEAR_NOTIFICATIONS, refined = false))
        assertTrue(Protocol.hasPermission(legacy, Protocol.PERMISSION_VOLUME_CONTROL, refined = false))
        assertFalse(Protocol.hasPermission(legacy, Protocol.PERMISSION_MAIN_WINDOW_LAYOUT, refined = false))
        assertFalse(Protocol.hasRefinedBits(legacy))
    }

    @Test
    fun `refined server checks the independent bit`() {
        val sendOnly = Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_POWER_CONTROL
        assertFalse(Protocol.hasPermission(sendOnly, Protocol.PERMISSION_CLEAR_NOTIFICATIONS, refined = true))
        assertFalse(Protocol.hasPermission(sendOnly, Protocol.PERMISSION_VOLUME_CONTROL, refined = true))
        assertTrue(Protocol.hasPermission(Protocol.PERMISSION_VOLUME_CONTROL, Protocol.PERMISSION_VOLUME_CONTROL, refined = true))
        assertTrue(Protocol.hasRefinedBits(Protocol.PERMISSION_VOLUME_CONTROL))
    }

    @Test
    fun `user profile detects refined server`() {
        val legacy = UserProfile(permissions = Protocol.PERMISSION_SEND_NOTIFICATIONS)
        assertFalse(legacy.usesRefinedPermissions)
        assertTrue(legacy.can(Protocol.PERMISSION_CLEAR_NOTIFICATIONS))

        // 新服务端会下发 managementPermissions（可能为 0），此时不再按旧位放行。
        val refined = legacy.copy(managementPermissions = 0)
        assertTrue(refined.usesRefinedPermissions)
        assertFalse(refined.can(Protocol.PERMISSION_CLEAR_NOTIFICATIONS))
        assertTrue(refined.can(Protocol.PERMISSION_SEND_NOTIFICATIONS))

        val classBits = UserProfile(classes = listOf(ClassSummary(id = "c", permissions = Protocol.PERMISSION_PULL_SCHEDULE)))
        assertTrue(classBits.usesRefinedPermissions)
    }

    @Test
    fun `pull schedule uses the class permission on refined servers`() {
        val allowed = UserProfile(
            managementPermissions = 0,
            classes = listOf(ClassSummary(id = "a", permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_PULL_SCHEDULE)),
        )
        assertTrue(allowed.canPullScheduleFor("a"))
        assertFalse(allowed.canPullScheduleFor("missing"))

        val classHeadWithoutBit = UserProfile(
            managementPermissions = 0,
            classes = listOf(ClassSummary(id = "a", roleKind = Protocol.ROLE_KIND_CLASS_ADMINISTRATOR, permissions = Protocol.PERMISSION_VIEW_CURRENT)),
        )
        assertFalse(classHeadWithoutBit.canPullScheduleFor("a"))
    }

    @Test
    fun `pull schedule falls back to class head on old servers`() {
        val head = UserProfile(classes = listOf(ClassSummary(id = "a", roleKind = Protocol.ROLE_KIND_CLASS_ADMINISTRATOR, permissions = 1)))
        assertTrue(head.canPullScheduleFor("a"))
        val student = UserProfile(classes = listOf(ClassSummary(id = "a", roleName = "学生", permissions = 1)))
        assertFalse(student.canPullScheduleFor("a"))
        assertTrue(UserProfile(role = Protocol.ROLE_ADMIN).canPullScheduleFor(null))
    }

    @Test
    fun `assignable permissions exclude base and display name bits`() {
        assertFalse(Protocol.PERMISSION_ASSIGNABLE and Protocol.PERMISSION_VIEW_CURRENT != 0)
        assertFalse(Protocol.PERMISSION_ASSIGNABLE and Protocol.PERMISSION_CHANGE_DISPLAY_NAME != 0)
        assertTrue(Protocol.PERMISSION_ASSIGNABLE and Protocol.PERMISSION_VIEW_LESSON_STATS != 0)
    }
}
