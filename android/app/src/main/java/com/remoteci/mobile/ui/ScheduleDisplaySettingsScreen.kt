package com.remoteci.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.SubjectColorItem
import com.remoteci.mobile.data.SubjectColorStore
import com.remoteci.mobile.data.WatchSettings
import kotlinx.coroutines.launch

/** 课表与日程共用的显示设置：开关只保存在本机，学科颜色按账号同步（另一页）。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScheduleDisplayScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit, onOpen: (Screen) -> Unit) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("显示设置") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            ConnectedListCard {
                AppListItem(
                    title = "学科颜色",
                    supporting = "按账号保存，与网页端同步",
                    leading = Icons.Rounded.Palette,
                    onClick = { onOpen(Screen.SubjectColors) },
                )
            }
            Text("显示内容", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            ConnectedListCard {
                val toggles = listOf(
                    Triple("显示老师", "班级课表中显示授课老师", settings.scheduleShowTeacher) to { value: Boolean -> settings.copy(scheduleShowTeacher = value) },
                    Triple("显示班级", "我的日程中显示上课班级", settings.scheduleShowClass) to { value: Boolean -> settings.copy(scheduleShowClass = value) },
                    Triple("显示地点", "显示科目地点或自定义课程的地点", settings.scheduleShowLocation) to { value: Boolean -> settings.copy(scheduleShowLocation = value) },
                    Triple("自动合并连堂", "表格中相邻的同一门课合成一块", settings.scheduleMergeConsecutive) to { value: Boolean -> settings.copy(scheduleMergeConsecutive = value) },
                )
                toggles.forEachIndexed { index, (row, update) ->
                    ToggleRow(row.first, row.second, row.third, index, toggles.size) { onPersist(update(it)) }
                }
            }
            Text("显示周末（表格）", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(24.dp))) {
                listOf(
                    Triple("auto", "自动", "本周周末有课时显示"),
                    Triple("always", "始终显示", null),
                    Triple("never", "始终隐藏", null),
                ).forEach { (id, label, supporting) ->
                    ListItem(
                        headlineContent = { Text(label) },
                        supportingContent = supporting?.let { value -> { Text(value) } },
                        trailingContent = { RadioButton(selected = settings.scheduleWeekendMode == id, onClick = null) },
                        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
                        modifier = Modifier.fillMaxWidth().clickable { onPersist(settings.copy(scheduleWeekendMode = id)) },
                    )
                }
            }
            Text(
                "以上设置只保存在这台手机上，课表与日程共用；表格与列表视图在课表页右上角切换。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun ToggleRow(title: String, supporting: String, checked: Boolean, index: Int, count: Int, onChange: (Boolean) -> Unit) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = { Text(supporting) },
        trailingContent = { Switch(checked = checked, onCheckedChange = onChange) },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
        modifier = Modifier.fillMaxWidth().clip(connectedShape(index, count)).clickable { onChange(!checked) },
    )
}

/** 学科颜色：列出可见课表与日程中的学科，点开后从色板选择，或恢复默认。 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SubjectColorsScreen(onBack: () -> Unit, snackbar: SnackbarHostState) {
    val response by SubjectColorStore.state.collectAsState()
    val scope = rememberCoroutineScope()
    var loading by remember { mutableStateOf(true) }
    var editing by remember { mutableStateOf<SubjectColorItem?>(null) }
    val dark = MaterialTheme.colorScheme.surface.luminance() < 0.5f
    LaunchedEffect(Unit) {
        if (!SubjectColorStore.refresh()) snackbar.showSnackbar("暂时无法同步学科颜色，显示的是上次保存的配色")
        loading = false
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("学科颜色") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (response.items.isEmpty()) {
            EmptyState(if (loading) "正在读取学科" else "还没有可设置颜色的学科", "课表或日程中出现的学科会列在这里。")
        } else {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                item {
                    Text("颜色按账号保存，网页端与其他手机同步显示。", style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 8.dp))
                }
                items(response.items, key = { it.name }) { item ->
                    val (background, foreground) = subjectColorPair(item.color, dark)
                    ListItem(
                        headlineContent = {
                            Box(Modifier.clip(RoundedCornerShape(10.dp)).background(background).padding(horizontal = 10.dp, vertical = 4.dp)) {
                                Text(item.name, color = foreground, fontWeight = FontWeight.Bold)
                            }
                        },
                        supportingContent = if (item.custom) ({ Text("已自定义") }) else null,
                        trailingContent = { Box(Modifier.size(22.dp).clip(CircleShape).background(hexColor(item.color))) },
                        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
                        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).clickable { editing = item },
                    )
                }
            }
        }
    }
    editing?.let { item ->
        val palette = response.palette.ifEmpty { SubjectColors.palette }
        ModalBottomSheet(onDismissRequest = { editing = null }) {
            Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp).padding(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text(item.name, style = MaterialTheme.typography.titleLarge)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    palette.forEach { color ->
                        val selected = color.equals(item.color, ignoreCase = true)
                        Box(
                            Modifier.size(40.dp).clip(CircleShape).background(hexColor(color))
                                .then(if (selected) Modifier.border(3.dp, MaterialTheme.colorScheme.onSurface, CircleShape) else Modifier)
                                .clickable {
                                    editing = null
                                    scope.launch {
                                        runCatching { SubjectColorStore.set(item.name, color) }
                                            .onFailure { snackbar.showSnackbar("保存失败：${it.message ?: "请检查网络后重试"}") }
                                    }
                                },
                            contentAlignment = Alignment.Center,
                        ) {
                            if (selected) Icon(Icons.Rounded.Check, contentDescription = "当前颜色", tint = MaterialTheme.colorScheme.surface)
                        }
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedButton(
                        enabled = item.custom,
                        onClick = {
                            editing = null
                            scope.launch {
                                runCatching { SubjectColorStore.reset(item.name) }
                                    .onFailure { snackbar.showSnackbar("恢复失败：${it.message ?: "请检查网络后重试"}") }
                            }
                        },
                    ) { Text("恢复默认") }
                    Spacer(Modifier.size(12.dp))
                    Text("修改颜色需要联网", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Spacer(Modifier.height(4.dp))
            }
        }
    }
}
