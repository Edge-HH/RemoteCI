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
import androidx.compose.material.icons.rounded.Cloud
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.CloudSync
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
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
import androidx.compose.runtime.LaunchedEffect
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
import com.remoteci.mobile.data.AdminApi
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
            .clip(RoundedCornerShape(24.dp))
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
    /** 图标底色与图标颜色；课表列表用学科颜色，省略时为主题色。 */
    leadingContainerColor: Color? = null,
    leadingContentColor: Color? = null,
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
                Modifier.size(44.dp).clip(RoundedCornerShape(16.dp)).background(leadingContainerColor ?: MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Icon(leading, contentDescription = null, tint = leadingContentColor ?: MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(24.dp))
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

/**
 * 底栏四个板块共用的顶栏：左上角是标题和当前班级，多班级账号点击这一整块即可换班；
 * [showConnection] 时标题右侧显示云朵连接状态图标（“今天”页）。右侧固定为
 * [actions]（板块自己的操作，如刷新）、扫码登录网页版和通知收件箱，保证全局入口在每个板块位置一致。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeTopBar(
    title: String,
    snackbar: SnackbarHostState,
    onOpen: (Screen) -> Unit,
    showConnection: Boolean = false,
    actions: @Composable RowScope.() -> Unit = {},
) {
    val classes by ConnectionManager.classes.collectAsState()
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val switchable = classes.size > 1
    var picking by remember { mutableStateOf(false) }
    // 班级列表缺失（局域网镜像、旧服务端）时不显示班名。
    val className = classes.firstOrNull { it.id == currentClassId }?.name?.takeIf(String::isNotBlank)
        ?: if (switchable) "未选择班级" else null
    TopAppBar(
        title = {
            Column(
                Modifier.clip(RoundedCornerShape(12.dp)).clickable(enabled = switchable, onClickLabel = "切换班级") { picking = true },
            ) {
                // 云朵图标只跟标题一行垂直居中，不随下方班名把整块撑高。
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(title, style = MaterialTheme.typography.titleLarge)
                    if (showConnection) ConnectionStatusIcon(onOpen)
                }
                className?.let {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            it,
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        if (switchable) {
                            Icon(
                                Icons.Rounded.ArrowDropDown,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.size(20.dp),
                            )
                        }
                    }
                }
            }
        },
        actions = {
            actions()
            // 扫描 WebUI 登录页的“手机扫码”二维码，确认后电脑浏览器以当前账号登录。
            WebLoginScanAction(snackbar)
            IconButton({ onOpen(Screen.Inbox) }) { Icon(Icons.Rounded.Notifications, contentDescription = "通知") }
        },
        colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.surface),
    )
    if (picking) ClassSwitchDialog(classes, currentClassId, onDismiss = { picking = false })
}

/**
 * 云朵连接状态图标：云端/局域网已连接、连接中、失败、未连接各用不同图标与颜色。
 * 点击展开详情（连接方式、课表同步时间、手表是否在线），并可进入“服务器与连接方式”。
 */
@Composable
private fun ConnectionStatusIcon(onOpen: (Screen) -> Unit) {
    val connection by ConnectionManager.state.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val pull by ConnectionManager.schedulePullState.collectAsState()
    val indicator = connectionIndicator(connection)
    var expanded by remember { mutableStateOf(false) }
    var watchConnections by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(connection, expanded) {
        if (expanded) watchConnections = runCatching { AdminApi.adminStatus().watchConnections }.getOrNull()
    }
    val colors = MaterialTheme.colorScheme
    val (icon, tint) = when (indicator) {
        ConnectionIndicator.Cloud -> Icons.Rounded.CloudDone to colors.primary
        ConnectionIndicator.Lan -> Icons.Rounded.Cloud to colors.tertiary
        ConnectionIndicator.Connecting -> Icons.Rounded.CloudSync to colors.onSurfaceVariant
        ConnectionIndicator.Error -> Icons.Rounded.CloudOff to colors.error
        ConnectionIndicator.Offline -> Icons.Rounded.CloudOff to colors.onSurfaceVariant
    }
    Box {
        // 比标准 IconButton 小，放在标题行里不增加行高。
        Box(
            Modifier.size(32.dp).clip(CircleShape).clickable(onClickLabel = "连接详情") { expanded = true },
            contentAlignment = Alignment.Center,
        ) { Icon(icon, contentDescription = indicator.label, tint = tint, modifier = Modifier.size(20.dp)) }
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            val syncText = when (val current = pull) {
                is ConnectionManager.SchedulePullState.Pulling -> current.message
                is ConnectionManager.SchedulePullState.Success -> current.message
                is ConnectionManager.SchedulePullState.Error -> current.message
                else -> formatLocalSyncTime(snapshot?.generatedAt)?.let { "课表已于 $it 同步" } ?: "尚未同步课表"
            }
            Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    (connection as? ConnectionManager.State.Error)?.message ?: indicator.label,
                    style = MaterialTheme.typography.titleSmall,
                    color = if (indicator == ConnectionIndicator.Error) colors.error else colors.onSurface,
                )
                Text(syncText, style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant)
                watchConnections?.let {
                    Text(if (it > 0) "手表已连接" else "手表未连接", style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant)
                }
            }
            DropdownMenuItem(
                text = { Text("服务器与连接方式") },
                onClick = { expanded = false; onOpen(Screen.Connection) },
            )
        }
    }
}

/** 快捷操作磁贴：图标、标题和一行状态说明，用于控制页顶部和“今天”页的快捷控制。 */
@Composable
fun QuickActionTile(
    title: String,
    icon: ImageVector,
    modifier: Modifier = Modifier,
    supporting: String? = null,
    onClick: () -> Unit,
) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.97f else 1f, label = "tile-press")
    Column(
        modifier
            .scale(scale)
            .clip(RoundedCornerShape(24.dp))
            .background(MaterialTheme.colorScheme.surfaceContainerLow)
            .clickable(interactionSource = source, indication = null, onClick = onClick)
            .heightIn(min = 96.dp)
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Box(
            Modifier.size(40.dp).clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.primaryContainer),
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(22.dp))
        }
        Text(title, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        supporting?.let {
            Text(
                it,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** 把磁贴按 [columns] 列排成网格；最后一行不足时用空位补齐，保持磁贴等宽。 */
@Composable
fun <T> QuickActionGrid(
    items: List<T>,
    modifier: Modifier = Modifier,
    columns: Int = 2,
    tile: @Composable (item: T, modifier: Modifier) -> Unit,
) {
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items.chunked(columns).forEach { rowItems ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                rowItems.forEach { tile(it, Modifier.weight(1f)) }
                repeat(columns - rowItems.size) { Spacer(Modifier.weight(1f)) }
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
