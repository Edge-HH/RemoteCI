package com.remoteci.mobile.ui

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.calendar.CalendarFeed
import com.remoteci.mobile.calendar.CalendarSync
import com.remoteci.mobile.calendar.CalendarSyncSettings
import com.remoteci.mobile.data.ClassSummary
import com.remoteci.mobile.data.ConnectionManager
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

/** 账号与设置中的“系统日历”：与网页端一致，分别订阅我的日程和固定班级的课表。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun CalendarSyncScreen(onBack: () -> Unit, snackbar: SnackbarHostState) {
    val classes by ConnectionManager.classes.collectAsState()
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("系统日历") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("课程写入本机系统日历，由日历应用按课前提醒通知。两种日历相互独立，修改和取消以 RemoteCI 为准。",
                style = MaterialTheme.typography.bodyMedium)
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(vertical = 12.dp)) {
                    Text("我的日程", Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.titleSmall)
                    Text("自己的任教课程和自定义课程", Modifier.padding(horizontal = 16.dp),
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    CalendarSyncRow(snackbar, 0, CalendarFeed.Personal)
                }
            }
            if (classes.isNotEmpty()) Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(vertical = 12.dp)) {
                    Text("班级课表", Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.titleSmall)
                    Text("固定同步所选班级，之后切换查看的班级不影响该日历", Modifier.padding(horizontal = 16.dp),
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    CalendarSyncRow(snackbar, 0, CalendarFeed.Class, classes.firstOrNull { it.id == currentClassId })
                }
            }
            Text("请在系统日历中保持这些日历可见并开启日历通知。日历和 RemoteCI 通知都开启课前提醒时可能重复提示。",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
internal fun CalendarSyncRow(snackbar: SnackbarHostState, refresh: Int, feed: CalendarFeed, defaultClass: ClassSummary? = null) {
    val context = LocalContext.current.applicationContext
    val user by ConnectionManager.currentUser.collectAsState()
    val userId = user?.id ?: return
    val server = ConnectionManager.restBaseUrl() ?: return
    val classes by ConnectionManager.classes.collectAsState()
    val scope = rememberCoroutineScope()
    val busy by CalendarSync.busy.collectAsState()
    val statuses by CalendarSync.status.collectAsState()
    var show by remember(server, userId) { mutableStateOf(false) }
    var automatic by remember(server, userId) { mutableStateOf(true) }
    var reminder by remember(server, userId) { mutableStateOf(10) }
    var classId by remember(server, userId) { mutableStateOf<String?>(null) }
    // 状态变化后重新读取，用于显示班级课表当前绑定的班级。
    val saved = remember(server, userId, statuses, show) { CalendarSync.settings(context, feed, server, userId) }
    LaunchedEffect(server, userId, refresh, feed) {
        while (true) {
            CalendarSync.sync(context, feed, onlyIfEnabled = true)
            delay(60_000)
        }
    }

    fun saveAndSync() {
        // 权限弹窗期间退出登录或切换服务器，不沿用旧账号的导入操作。
        if (ConnectionManager.currentUser.value?.id != userId || ConnectionManager.restBaseUrl() != server) return
        val className = classes.firstOrNull { it.id == classId }?.name
        scope.launch {
            try {
                CalendarSync.configure(context, feed, server, userId, CalendarSyncSettings(automatic, reminder,
                    classId.takeIf { feed == CalendarFeed.Class }, className.takeIf { feed == CalendarFeed.Class }))
                show = false
                val success = CalendarSync.sync(context, feed)
                snackbar.showSnackbar(CalendarSync.status.value[feed] ?: if (success) "日程已导入" else "同步未完成，请重试")
            } catch (error: kotlinx.coroutines.CancellationException) {
                throw error
            } catch (error: Exception) {
                snackbar.showSnackbar(error.message ?: "无法导入日程")
            }
        }
    }

    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (CalendarSync.hasPermission(context)) saveAndSync()
        else scope.launch { snackbar.showSnackbar("未获得日历读写权限，可在系统设置中授权后重试") }
    }

    val idle = when (feed) {
        CalendarFeed.Personal -> "将个人日程同步到系统日历"
        CalendarFeed.Class -> saved.className?.let { "已绑定 $it" } ?: "将班级课表同步到系统日历"
    }
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(statuses[feed] ?: idle, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
        TextButton(enabled = !busy && (feed == CalendarFeed.Personal || classes.isNotEmpty()), onClick = {
            automatic = saved.automatic
            reminder = saved.reminderMinutes
            classId = saved.classId?.takeIf { id -> classes.any { it.id == id } } ?: defaultClass?.id ?: classes.firstOrNull()?.id
            show = true
        }) { Text(if (busy) "同步中…" else if (feed == CalendarFeed.Class) "同步课表" else "导入日程") }
    }

    if (show) AlertDialog(
        onDismissRequest = { show = false },
        title = { Text(if (feed == CalendarFeed.Class) "同步班级课表" else "导入日程") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                if (feed == CalendarFeed.Class) {
                    Text("在系统日历创建“RemoteCI 班级名”，固定同步所选班级的未来七日课表。改选其他班级时，原班级未结束的课程会被移除。")
                    if (classes.size > 1) {
                        Text("班级")
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            classes.forEach { item ->
                                FilterChip(selected = classId == item.id, onClick = { classId = item.id }, label = { Text(item.name) })
                            }
                        }
                    }
                } else {
                    Text("在系统日历创建“RemoteCI 我的日程”，由日历应用提醒。导入当前课程，修改和取消以 RemoteCI 日程为准。")
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("自动同步", Modifier.weight(1f))
                    Switch(checked = automatic, onCheckedChange = { automatic = it })
                }
                Text("开启后，在线时随课表刷新，后台约每 15 分钟同步（可能受系统省电影响）。关闭后保留已导入事件。", style = MaterialTheme.typography.bodySmall)
                Text("课前提醒")
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(0, 5, 10, 15, 30).forEach { minutes ->
                        FilterChip(selected = reminder == minutes, onClick = { reminder = minutes },
                            label = { Text(if (minutes == 0) "开始时" else "$minutes 分钟") })
                    }
                }
                Text("请在系统日历中保持该日历可见，并开启日历通知。再次同步会更新已有课程。", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            TextButton(enabled = feed == CalendarFeed.Personal || classId != null, onClick = {
                if (CalendarSync.hasPermission(context)) saveAndSync()
                else permissions.launch(arrayOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR))
            }) { Text("同步并保存") }
        },
        dismissButton = { TextButton(onClick = { show = false }) { Text("取消") } },
    )
}
