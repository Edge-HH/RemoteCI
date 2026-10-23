package com.remoteci.mobile.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ArrowDropDown
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.ClassSummary
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.classDisplayName

fun connectedShape(index: Int, count: Int, outer: Dp = 28.dp, inner: Dp = 8.dp): Shape {
    if (count <= 1) return RoundedCornerShape(outer)
    return when (index) {
        0 -> RoundedCornerShape(topStart = outer, topEnd = outer, bottomStart = inner, bottomEnd = inner)
        count - 1 -> RoundedCornerShape(topStart = inner, topEnd = inner, bottomStart = outer, bottomEnd = outer)
        else -> RoundedCornerShape(inner)
    }
}

fun connectedButtonShape(index: Int, count: Int, outer: Dp = 28.dp, inner: Dp = 8.dp): Shape {
    if (count <= 1) return RoundedCornerShape(outer)
    return when (index) {
        0 -> RoundedCornerShape(topStart = outer, bottomStart = outer, topEnd = inner, bottomEnd = inner)
        count - 1 -> RoundedCornerShape(topStart = inner, bottomStart = inner, topEnd = outer, bottomEnd = outer)
        else -> RoundedCornerShape(inner)
    }
}

@Composable
fun Pressable(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable () -> Unit,
) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.97f else 1f, label = "press-scale")
    Box(
        modifier = modifier
            .scale(scale)
            .clip(RoundedCornerShape(20.dp))
            .clickable(enabled = enabled, interactionSource = source, indication = null, onClick = onClick),
    ) { content() }
}

/** 列表分组标题：设置页、控制页按主题分组时放在每组卡片上方。 */
@Composable
fun ListSectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        modifier = modifier.padding(start = 20.dp, end = 20.dp, top = 16.dp, bottom = 8.dp),
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
    )
}

@Composable
fun ConnectedListCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(3.dp), content = content)
}

@Composable
fun AppListItem(
    title: String,
    supporting: String? = null,
    leading: ImageVector,
    trailing: ImageVector? = null,
    trailingText: String? = null,
    switchChecked: Boolean? = null,
    onSwitch: ((Boolean) -> Unit)? = null,
    index: Int = 0,
    count: Int = 1,
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
) {
    val colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)
    val shape = connectedShape(index, count)
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.97f else 1f, label = "list-item-press")
    ListItem(
        headlineContent = { Text(title, style = MaterialTheme.typography.bodyLarge) },
        supportingContent = supporting?.let { { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) } },
        leadingContent = {
            Box(
                Modifier.size(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Icon(leading, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(24.dp))
            }
        },
        trailingContent = {
            when {
                switchChecked != null -> Switch(checked = switchChecked, onCheckedChange = onSwitch)
                trailing != null -> Icon(trailing, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                trailingText != null -> Text(trailingText, style = MaterialTheme.typography.labelLarge)
            }
        },
        colors = colors,
        modifier = modifier
            .fillMaxWidth()
            .scale(scale)
            .clip(shape)
            .then(
                if (onClick != null) Modifier.clickable(
                    interactionSource = source,
                    indication = null,
                    onClick = onClick,
                ) else Modifier,
            )
            .heightIn(min = 72.dp),
    )
}

@Composable
fun ConnectedButtons(
    modifier: Modifier = Modifier,
    firstLabel: String,
    secondLabel: String,
    onFirst: () -> Unit,
    onSecond: () -> Unit,
    firstFilled: Boolean = true,
) {
    Row(modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
        val firstShape = connectedButtonShape(0, 2)
        val secondShape = connectedButtonShape(1, 2)
        if (firstFilled) {
            Button(
                onClick = onFirst,
                modifier = Modifier.weight(1f).height(56.dp),
                shape = firstShape,
            ) { Text(firstLabel) }
            FilledTonalButton(
                onClick = onSecond,
                modifier = Modifier.weight(1f).height(56.dp),
                shape = secondShape,
                colors = ButtonDefaults.filledTonalButtonColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
            ) { Text(secondLabel) }
        } else {
            OutlinedButton(
                onClick = onFirst,
                modifier = Modifier.weight(1f).height(56.dp),
                shape = firstShape,
            ) { Text(firstLabel) }
            Button(
                onClick = onSecond,
                modifier = Modifier.weight(1f).height(56.dp),
                shape = secondShape,
            ) { Text(secondLabel) }
        }
    }
}

@Composable
fun ScreenScaffold(
    snackbarHostState: SnackbarHostState,
    bottomBar: @Composable () -> Unit = {},
    content: @Composable (PaddingValues) -> Unit,
) {
    androidx.compose.material3.Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        bottomBar = bottomBar,
        containerColor = MaterialTheme.colorScheme.surface,
        content = content,
    )
}

@Composable
fun EmptyState(title: String, body: String, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Text(title, style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(8.dp))
        Text(body, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun PermissionHint(text: String) {
    Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp))
}

/**
 * 当前班级芯片：显示真实班名；多班级账号带下拉箭头，点击展开班级菜单直接换班，单班级账号只显示名称。
 * [selected] 不为 null 时作为筛选芯片使用（课表页“我的日程 / 班级课表”）：未选中时点击只切回班级视图，
 * 选中后再点一次才展开班级菜单。
 */
@Composable
fun ClassSwitcherChip(
    modifier: Modifier = Modifier,
    selected: Boolean? = null,
    onSelect: () -> Unit = {},
) {
    val classes by ConnectionManager.classes.collectAsState()
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val switchable = classes.size > 1
    var expanded by remember { mutableStateOf(false) }
    val label: @Composable () -> Unit = {
        Text(classDisplayName(classes, currentClassId, fallback = "班级课表"), maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
    val arrow: (@Composable () -> Unit)? = if (switchable) {
        { Icon(Icons.Rounded.ArrowDropDown, contentDescription = "切换班级", modifier = Modifier.size(FilterChipDefaults.IconSize)) }
    } else null
    Box(modifier) {
        if (selected == null) {
            AssistChip(onClick = { if (switchable) expanded = true }, label = label, trailingIcon = arrow)
        } else {
            FilterChip(
                selected = selected,
                onClick = { if (!selected) onSelect() else if (switchable) expanded = true },
                label = label,
                trailingIcon = arrow,
            )
        }
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            classes.forEach { classroom ->
                DropdownMenuItem(
                    text = { Text(classroom.name) },
                    onClick = {
                        expanded = false
                        ConnectionManager.switchClass(classroom.id)
                    },
                    trailingIcon = if (classroom.id == currentClassId) {
                        { Icon(Icons.Rounded.Check, contentDescription = "当前班级") }
                    } else null,
                )
            }
        }
    }
}

/** 多班级账号的换班对话框：选中即切换，并关闭对话框。 */
@Composable
fun ClassSwitchDialog(classes: List<ClassSummary>, currentClassId: String?, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("切换班级") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                classes.forEach { classroom ->
                    ListItem(
                        headlineContent = { Text(classroom.name) },
                        supportingContent = listOfNotNull(classroom.groupName, classroom.roleName)
                            .joinToString(" · ")
                            .takeIf { it.isNotBlank() }
                            ?.let { { Text(it) } },
                        trailingContent = { RadioButton(selected = classroom.id == currentClassId, onClick = null) },
                        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                        modifier = Modifier.fillMaxWidth().clickable {
                            ConnectionManager.switchClass(classroom.id)
                            onDismiss()
                        },
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("关闭") } },
    )
}
