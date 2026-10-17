package com.remoteci.mobile.ui

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import com.remoteci.mobile.calendar.CalendarSync
import com.remoteci.mobile.data.ConnectionManager
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

@Composable
internal fun PersonalCalendarImport(snackbar: SnackbarHostState, refresh: Int) {
    val context = LocalContext.current.applicationContext
    val user by ConnectionManager.currentUser.collectAsState()
    val userId = user?.id ?: return
    val server = ConnectionManager.restBaseUrl() ?: return
    val scope = rememberCoroutineScope()
    val busy by CalendarSync.busy.collectAsState()
    val status by CalendarSync.status.collectAsState()
    var show by remember(server, userId) { mutableStateOf(false) }
    var automatic by remember(server, userId) { mutableStateOf(true) }
    var reminder by remember(server, userId) { mutableStateOf(10) }
    LaunchedEffect(server, userId, refresh) {
        while (true) {
            CalendarSync.syncIfEnabled(context)
            delay(60_000)
        }
    }

    fun saveAndSync() {
        // 权限弹窗期间退出登录或切换服务器，不沿用旧账号的导入操作。
        if (ConnectionManager.currentUser.value?.id != userId || ConnectionManager.restBaseUrl() != server) return
        scope.launch {
            try {
                CalendarSync.configure(context, server, userId, com.remoteci.mobile.calendar.CalendarSyncSettings(automatic, reminder))
                show = false
                val success = CalendarSync.sync(context)
                snackbar.showSnackbar(CalendarSync.status.value ?: if (success) "日程已导入" else "同步未完成，请重试")
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

    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(status ?: "将个人日程同步到系统日历", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
        TextButton(enabled = !busy, onClick = {
            val settings = CalendarSync.settings(context, server, userId)
            automatic = settings.automatic
            reminder = settings.reminderMinutes
            show = true
        }) { Text(if (busy) "同步中…" else "导入日程") }
    }

    if (show) AlertDialog(
        onDismissRequest = { show = false },
        title = { Text("导入日程") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("在系统日历创建“RemoteCI 我的日程”，由日历应用提醒。导入当前课程，修改和取消以 RemoteCI 日程为准。")
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("自动同步", Modifier.weight(1f))
                    Switch(checked = automatic, onCheckedChange = { automatic = it })
                }
                Text("开启后，在线时随日程刷新，后台约每 15 分钟同步（可能受系统省电影响）。关闭后保留已导入事件。", style = MaterialTheme.typography.bodySmall)
                Text("课前提醒")
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(0, 5, 10, 15, 30).forEach { minutes ->
                        FilterChip(selected = reminder == minutes, onClick = { reminder = minutes },
                            label = { Text(if (minutes == 0) "开始时" else "$minutes 分钟") })
                    }
                }
                Text("请在系统日历中保持该日历可见，并开启日历通知。再次导入会更新已有课程。", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            TextButton(onClick = {
                if (CalendarSync.hasPermission(context)) saveAndSync()
                else permissions.launch(arrayOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR))
            }) { Text("导入并保存") }
        },
        dismissButton = { TextButton(onClick = { show = false }) { Text("取消") } },
    )
}
