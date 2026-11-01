package com.remoteci.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.DragHandle
import androidx.compose.material.icons.rounded.Star
import androidx.compose.material.icons.rounded.StarBorder
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import com.remoteci.mobile.data.WatchSettings
import kotlin.math.abs
import kotlin.math.roundToInt

/** 自定义页每行的高度：拖动时按这个高度折算移动了几行，因此固定等高。 */
private val ControlRowHeight = 76.dp

/** 自定义页的编辑状态：控制项顺序与常用项。拖动时先改这里，随每次移动立即保存。 */
private data class ControlEditorState(
    val order: List<ControlEntry>,
    val favorites: List<ControlEntry>,
)

/**
 * “控制 → 自定义控制页”：按住每行右侧的把手上下拖动调整顺序，星标决定哪些项是“常用”。
 * 扩展功能与内置控制项一起列出，可以单独设为常用；常用项显示在控制页顶部，
 * 同时出现在“今天”页的快捷控制里。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ControlLayoutScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    val entries = rememberVisibleControlEntries()
    // 拖动回调在 Composition 之外执行，用它读到最新的设置再保存，避免连续拖动时用旧副本覆盖。
    val latestSettings by rememberUpdatedState(settings)

    fun savedState(): ControlEditorState {
        val order = orderedControlEntries(entries, settings.controlOrder)
        return ControlEditorState(order, favoriteControlEntries(order, settings.controlFavorites))
    }

    fun defaultState(): ControlEditorState {
        val order = orderedControlEntries(entries, emptyList())
        return ControlEditorState(order, favoriteControlEntries(order, null))
    }

    var editor by remember(entries) { mutableStateOf(savedState()) }

    fun persist(next: ControlEditorState) {
        editor = next
        val orderKeys = next.order.map { it.key }
        val favoriteKeys = next.favorites.map { it.key }
        // 暂时不可用的项（插件离线、账号可见性变化）不在编辑列表里，但保留原来的位置与常用设置，
        // 重新可用后不需要再设一遍；用户取消的常用项在可用项里，不会被带回来。
        val availableKeys = entries.map { it.key }.toSet()
        val staleOrder = latestSettings.controlOrder.filterNot { it in availableKeys || it in orderKeys }
        val staleFavorites = latestSettings.controlFavorites.orEmpty()
            .filterNot { it in availableKeys || it in favoriteKeys }
        onPersist(
            latestSettings.copy(
                controlOrder = orderKeys + staleOrder,
                controlFavorites = favoriteKeys + staleFavorites,
            ),
        )
    }

    /** 把 [entry] 在它所在的小节里移动 [steps] 个位置（正数向下）；返回实际移动的行数，供拖动补偿用。 */
    fun moveBy(entry: ControlEntry, steps: Int): Int {
        if (steps == 0) return 0
        val favoriteKeys = editor.favorites.map { it.key }.toSet()
        val inFavorites = entry.key in favoriteKeys
        var next = editor
        var remaining = abs(steps)
        while (remaining > 0) {
            val moved = if (inFavorites) {
                next.favorites.movedWithin(entry, up = steps < 0)
            } else {
                movedWithinControlGroup(next.order, favoriteKeys, entry, up = steps < 0)
            } ?: break
            next = if (inFavorites) next.copy(favorites = moved) else next.copy(order = moved)
            remaining--
        }
        if (next != editor) persist(next)
        return if (steps < 0) remaining - abs(steps) else abs(steps) - remaining
    }

    val favoriteKeys = editor.favorites.map { it.key }.toSet()
    val sections = buildList {
        if (editor.favorites.isNotEmpty()) add(ControlGroup("常用", editor.favorites))
        controlGroupNames.forEach { name ->
            val groupEntries = editor.order.filter { it.group == name && it.key !in favoriteKeys }
            if (groupEntries.isNotEmpty()) add(ControlGroup(name, groupEntries))
        }
    }
    val customized = settings.controlFavorites != null || settings.controlOrder.isNotEmpty()
    val rowHeightPx = with(LocalDensity.current) { ControlRowHeight.toPx() }
    var dragged by remember { mutableStateOf<ControlEntry?>(null) }
    var dragOffset by remember { mutableFloatStateOf(0f) }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("自定义控制页") },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
        )
        if (entries.isEmpty()) {
            EmptyState("没有可用的控制项", "这取决于账号权限，以及当前插件是否在线。")
        } else {
            // LazyColumn 用控制项 key 作 key：拖动过程中重排不会打断正在进行的拖拽手势。
            LazyColumn(
                Modifier.fillMaxSize().navigationBarsPadding(),
                contentPadding = PaddingValues(bottom = 24.dp),
            ) {
                item(key = "intro") {
                    Text(
                        "按住右侧把手上下拖动调整顺序。星标为“常用”的控制项（包括扩展功能）会显示在控制页顶部，并同步到“今天”页的快捷控制。",
                        modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                sections.forEach { section ->
                    item(key = "section-${section.name}") { ListSectionLabel(section.name) }
                    itemsIndexed(section.items, key = { _, entry -> entry.key }) { index, entry ->
                        val isDragged = dragged == entry
                        ControlItemRow(
                            entry = entry,
                            isFavorite = entry.key in favoriteKeys,
                            shape = connectedShape(index, section.items.size),
                            onToggleFavorite = {
                                persist(editor.copy(favorites = toggledControlFavorite(editor.favorites, entry)))
                            },
                            onMoveBy = { steps -> moveBy(entry, steps) },
                            // 被拖的那行跟着手指走，其余行让位时带一点位移动画。
                            modifier = Modifier
                                .zIndex(if (isDragged) 1f else 0f)
                                .then(if (isDragged) Modifier else Modifier.animateItem())
                                .graphicsLayer { translationY = if (isDragged) dragOffset else 0f },
                            dragHandleModifier = Modifier.pointerInput(entry.key) {
                                detectDragGestures(
                                    onDragStart = {
                                        dragged = entry
                                        dragOffset = 0f
                                    },
                                    onDragEnd = {
                                        dragged = null
                                        dragOffset = 0f
                                    },
                                    onDragCancel = {
                                        dragged = null
                                        dragOffset = 0f
                                    },
                                    onDrag = { change, amount ->
                                        change.consume()
                                        dragOffset += amount.y
                                        val steps = (dragOffset / rowHeightPx).roundToInt()
                                        if (steps != 0) {
                                            val applied = moveBy(entry, steps)
                                            // 到达小节两端时停在原地，不跟着手指继续漂移。
                                            if (applied == 0) dragOffset = 0f else dragOffset -= applied * rowHeightPx
                                        }
                                    },
                                )
                            },
                        )
                    }
                }
                if (customized) {
                    item(key = "reset") {
                        TextButton(
                            onClick = {
                                editor = defaultState()
                                onPersist(latestSettings.copy(controlFavorites = null, controlOrder = emptyList()))
                            },
                            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                        ) { Text("恢复默认顺序和常用项") }
                    }
                }
            }
        }
    }
}

/** 自定义页的一行：图标和标题，右侧是常用星标与拖动把手。扩展功能在标题下显示注册 id。 */
@Composable
private fun ControlItemRow(
    entry: ControlEntry,
    isFavorite: Boolean,
    shape: Shape,
    onToggleFavorite: () -> Unit,
    onMoveBy: (Int) -> Unit,
    modifier: Modifier = Modifier,
    dragHandleModifier: Modifier = Modifier,
) {
    ListItem(
        headlineContent = { Text(entry.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = (entry as? ControlEntry.Extension)?.let {
            {
                Text(
                    entry.id,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        },
        leadingContent = {
            Box(
                Modifier.size(44.dp).clip(RoundedCornerShape(16.dp)).background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Icon(entry.icon, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(24.dp))
            }
        },
        trailingContent = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconButton(onToggleFavorite) {
                    Icon(
                        if (isFavorite) Icons.Rounded.Star else Icons.Rounded.StarBorder,
                        contentDescription = if (isFavorite) "取消常用：${entry.title}" else "设为常用：${entry.title}",
                        tint = if (isFavorite) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                // 拖动排序只靠手势，读屏时用自定义操作仍能上下移动。
                Box(
                    dragHandleModifier
                        .size(48.dp)
                        .semantics(mergeDescendants = true) {
                            contentDescription = "拖动排序：${entry.title}"
                            customActions = listOf(
                                CustomAccessibilityAction("上移") { onMoveBy(-1); true },
                                CustomAccessibilityAction("下移") { onMoveBy(1); true },
                            )
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        Icons.Rounded.DragHandle,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
        modifier = modifier.fillMaxWidth().height(ControlRowHeight).clip(shape),
    )
}
