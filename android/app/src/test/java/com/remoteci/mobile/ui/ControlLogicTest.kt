package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ExtensionDefinition
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ControlLogicTest {
    private val allCaps = ControlItem.entries.map { it.capability }.toSet()
    private val allPermissions = ControlItem.entries.fold(0) { acc, item -> acc or item.permission }
    private val items = ControlItem.entries.toList()

    private fun extension(id: String, name: String = id) = ExtensionDefinition(id = id, displayName = name)

    private val demoOne = extension("demo.one", "演示一")
    private val demoTwo = extension("demo.two", "演示二")
    private val entries = controlEntries(items, listOf(demoOne, demoTwo))
    private val builtins = items.map { ControlEntry.Builtin(it) }

    @Test
    fun `all items are visible with every permission and capability`() {
        assertEquals(items, visibleControlItems(allPermissions, refined = true, capabilities = allCaps))
    }

    @Test
    fun `items need both permission and plugin capability`() {
        val visible = visibleControlItems(
            Protocol.PERMISSION_SEND_NOTIFICATIONS or Protocol.PERMISSION_CAPTURE_SCREEN,
            refined = true,
            capabilities = setOf(Protocol.CAP_NOTIFICATION_SEND),
        )
        assertEquals(listOf(ControlItem.Notify), visible)
    }

    @Test
    fun `legacy servers grant clear notifications through send notifications`() {
        val visible = visibleControlItems(Protocol.PERMISSION_SEND_NOTIFICATIONS, refined = false, capabilities = allCaps)
        assertEquals(listOf(ControlItem.Notify, ControlItem.ClearNotifications), visible)
    }

    @Test
    fun `extensions are listed after the built-in items`() {
        assertEquals(builtins + listOf(ControlEntry.Extension(demoOne), ControlEntry.Extension(demoTwo)), entries)
        assertEquals("ext:demo.two", entries.last().key)
    }

    @Test
    fun `default layout uses the default favorites and lists extensions in the extension group`() {
        val layout = controlLayout(entries, savedOrder = emptyList(), savedFavorites = null)
        assertEquals(defaultControlFavorites.map { ControlEntry.Builtin(it) }, layout.favorites)
        assertEquals(4, layout.favorites.size)
        assertEquals(listOf("消息", "大屏", "扩展"), layout.groups.map { it.name })
        assertEquals(
            listOf("Voice", "Volume", "Power", "Extensions", "ext:demo.one", "ext:demo.two"),
            layout.rest.map { it.key },
        )
    }

    @Test
    fun `custom order applies inside each group and new entries go last`() {
        val layout = controlLayout(
            entries,
            savedOrder = listOf("ext:demo.two", "Extensions", "Volume"),
            savedFavorites = null,
        )
        assertEquals(listOf("Volume", "Power"), layout.groups.first { it.name == "大屏" }.items.map { it.key })
        // 扩展功能也可以和具体扩展互相调换位置。
        assertEquals(
            listOf("ext:demo.two", "Extensions", "ext:demo.one"),
            layout.groups.first { it.name == "扩展" }.items.map { it.key },
        )
    }

    @Test
    fun `an extension can be a favorite tile and leaves its group`() {
        val layout = controlLayout(
            entries,
            savedOrder = emptyList(),
            savedFavorites = listOf("ext:demo.two", "Notify"),
        )
        assertEquals(listOf("ext:demo.two", "Notify"), layout.favorites.map { it.key })
        assertEquals(
            listOf("Extensions", "ext:demo.one"),
            layout.groups.first { it.name == "扩展" }.items.map { it.key },
        )
    }

    @Test
    fun `saved empty favorites mean no tiles at all`() {
        val layout = controlLayout(entries, savedOrder = emptyList(), savedFavorites = emptyList())
        assertTrue(layout.favorites.isEmpty())
        assertEquals(entries.map { it.key }, layout.rest.map { it.key })
    }

    @Test
    fun `entries hidden by permissions stay out of the layout`() {
        val visible = visibleControlItems(
            Protocol.PERMISSION_SEND_NOTIFICATIONS,
            refined = true,
            capabilities = setOf(Protocol.CAP_NOTIFICATION_SEND),
        )
        val layout = controlLayout(
            controlEntries(visible, emptyList()),
            savedOrder = listOf("Volume", "Notify"),
            savedFavorites = listOf("Volume", "Notify"),
        )
        assertEquals(listOf("Notify"), layout.favorites.map { it.key })
        assertTrue(layout.groups.isEmpty())
    }

    @Test
    fun `extensions need the class permission and the account allow lists`() {
        val canRun = Protocol.PERMISSION_RUN_EXTENSIONS
        assertTrue(extensionVisibleForClass(user(), canRun, demoOne))
        assertTrue(extensionVisibleForClass(null, canRun, demoOne))
        assertTrue(!extensionVisibleForClass(user(), Protocol.PERMISSION_VIEW_CURRENT, demoOne))
        assertTrue(!extensionVisibleForClass(user(allowed = listOf("other")), canRun, demoOne))
        assertTrue(!extensionVisibleForClass(user(visible = listOf("other")), canRun, demoOne))
        assertTrue(extensionVisibleForClass(user(allowed = listOf("demo.one"), visible = listOf("demo.one")), canRun, demoOne))
    }

    @Test
    fun `moving inside a group skips favorites and other groups`() {
        val favoriteKeys = setOf("MainMenu")
        assertEquals(
            listOf(
                "Notify", "Voice", "ClearNotifications", "MainMenu",
                "Volume", "Screenshot", "Power", "Extensions", "ext:demo.one", "ext:demo.two",
            ),
            movedWithinControlGroup(entries, favoriteKeys, ControlEntry.Builtin(ControlItem.Volume), up = true)?.map { it.key },
        )
        // 扩展功能与内置项属于同一个“扩展”分组，可以互相调整顺序。
        assertEquals(
            listOf(
                "Notify", "Voice", "ClearNotifications", "MainMenu", "Screenshot",
                "Volume", "Power", "ext:demo.one", "Extensions", "ext:demo.two",
            ),
            movedWithinControlGroup(entries, setOf("ext:demo.two"), ControlEntry.Extension(demoOne), up = true)?.map { it.key },
        )
        assertNull(movedWithinControlGroup(entries, favoriteKeys, ControlEntry.Builtin(ControlItem.Notify), up = true))
        assertNull(movedWithinControlGroup(entries, favoriteKeys, ControlEntry.Extension(demoTwo), up = false))
    }

    @Test
    fun `favoriting appends a tile and unfavoriting restores the group position`() {
        val favorites = toggledControlFavorite(listOf(ControlEntry.Builtin(ControlItem.Notify)), ControlEntry.Extension(demoTwo))
        assertEquals(listOf("Notify", "ext:demo.two"), favorites.map { it.key })
        assertEquals(
            listOf("Notify"),
            toggledControlFavorite(favorites, ControlEntry.Extension(demoTwo)).map { it.key },
        )
    }

    @Test
    fun `favorites move one tile at a time`() {
        val tiles = listOf(ControlEntry.Builtin(ControlItem.Notify), ControlEntry.Extension(demoTwo))
        assertEquals(
            listOf("ext:demo.two", "Notify"),
            tiles.movedWithin(ControlEntry.Extension(demoTwo), up = true)?.map { it.key },
        )
        assertNull(tiles.movedWithin(ControlEntry.Builtin(ControlItem.Notify), up = true))
    }

    private fun user(allowed: List<String>? = null, visible: List<String>? = null) =
        UserProfile(allowedExtensionIds = allowed, visibleExtensionIds = visible)
}
