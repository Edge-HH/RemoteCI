package com.remoteci.mobile.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Campaign
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.Extension
import androidx.compose.material.icons.rounded.Mic
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.NotificationsActive
import androidx.compose.material.icons.rounded.NotificationsOff
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VolumeUp
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ClassEvent
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.ExtensionDefinition
import com.remoteci.mobile.data.ExtensionPolicyUpdate
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile
import com.remoteci.mobile.data.VoiceRecorder
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ControlListScreen(embedded: Boolean, onOpen: (Screen) -> Unit, onBack: (() -> Unit)?) {
    val user by ConnectionManager.currentUser.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val caps by ConnectionManager.availableCapabilities.collectAsState()
    data class Row(
        val title: String,
        val supporting: String,
        val icon: androidx.compose.ui.graphics.vector.ImageVector,
        val visible: Boolean,
        val screen: Screen?,
        val action: (() -> Unit)? = null,
    )
    val rows = listOf(
        Row("发送通知", "标题、正文、强调特效、音效和语音朗读", Icons.Rounded.NotificationsActive,
            user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true && Protocol.CAP_NOTIFICATION_SEND in caps, Screen.Notify),
        Row("发送语音", "最长 60 秒的语音消息", Icons.Rounded.Mic,
            user?.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) == true && Protocol.CAP_VOICE_MESSAGE_SEND in caps, Screen.Voice),
        Row("清除提醒", "清除 ClassIsland 当前提醒与手表提示", Icons.Rounded.NotificationsOff,
            user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true && Protocol.CAP_NOTIFICATION_CLEAR in caps, null,
            { ConnectionManager.clearNotifications() }),
        Row("老师来了", "显示提醒，等待一秒后自动清除", Icons.Rounded.Campaign,
            user?.has(Protocol.PERMISSION_TEACHER_COMING) == true && Protocol.CAP_TEACHER_COMING in caps, null,
            { ConnectionManager.teacherComing() }),
        Row("主界面", if (snapshot?.isMainMenuVisible == true) "当前显示中，点击可隐藏" else "当前已隐藏，点击可显示",
            Icons.Rounded.Visibility,
            user?.has(Protocol.PERMISSION_MAIN_MENU_CONTROL) == true && Protocol.CAP_MAIN_MENU_VISIBILITY in caps, Screen.MainMenu),
        Row("音量", "音量调节和切换静音", Icons.Rounded.VolumeUp,
            user?.has(Protocol.PERMISSION_POWER_CONTROL) == true && Protocol.CAP_VOLUME_CONTROL in caps, Screen.Volume),
        Row("电源控制", "关机、睡眠或休眠", Icons.Rounded.PowerSettingsNew,
            user?.has(Protocol.PERMISSION_POWER_CONTROL) == true && Protocol.CAP_POWER_CONTROL in caps, Screen.Power),
        Row("扩展功能", "运行已注册扩展", Icons.Rounded.Extension,
            user?.has(Protocol.PERMISSION_RUN_EXTENSIONS) == true && Protocol.CAP_EXTENSIONS_RUN in caps, Screen.Extensions),
    ).filter { it.visible }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("课堂控制", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = { IconButton(onClick = { onBack?.invoke() }) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
        )
        if (rows.isEmpty()) {
            EmptyState("没有可用的控制项", "这取决于账号权限，以及当前插件是否在线。")
        } else {
            ConnectedListCard(Modifier.padding(16.dp)) {
                rows.forEachIndexed { index, row ->
                    AppListItem(
                        title = row.title,
                        supporting = row.supporting,
                        leading = row.icon,
                        trailing = Icons.Rounded.ChevronRight,
                        index = index,
                        count = rows.size,
                        onClick = { row.screen?.let(onOpen) ?: row.action?.invoke() },
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotifyScreen(onBack: () -> Unit) {
    var title by remember { mutableStateOf("") }
    var message by remember { mutableStateOf("") }
    var effect by remember { mutableStateOf(true) }
    var sound by remember { mutableStateOf(false) }
    var speech by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("发送通知") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(title, { title = it }, label = { Text("标题") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(message, { message = it }, label = { Text("正文") }, modifier = Modifier.fillMaxWidth().height(140.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) { Text("强调特效"); Switch(effect, { effect = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) { Text("通知音效"); Switch(sound, { sound = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) { Text("语音朗读"); Switch(speech, { speech = it }) }
            Button(
                onClick = { ConnectionManager.sendNotification(title, message, effect, sound, speech) },
                modifier = Modifier.fillMaxWidth().height(56.dp),
                enabled = title.isNotBlank() || message.isNotBlank(),
            ) { Text("发送") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VoiceScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var seconds by remember { mutableIntStateOf(0) }
    var recording by remember { mutableStateOf(false) }
    var sending by remember { mutableStateOf(false) }
    var audio by remember { mutableStateOf<ByteArray?>(null) }
    val stop = remember { AtomicBoolean(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startRecord(scope, stop, { seconds = it }, { recording = it }, { audio = it })
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopAppBar(title = { Text("发送语音") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Text(if (recording) "正在录音 $seconds 秒 / 60" else audio?.let { "已录制 ${it.size / (16000 * 2)} 秒" } ?: "最多录制 60 秒，发送后由 ClassIsland 播放。")
        Button(
            onClick = {
                if (recording) {
                    stop.set(true)
                } else {
                    audio = null
                    val granted = ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
                    if (granted) startRecord(scope, stop, { seconds = it }, { recording = it }, { audio = it })
                    else permission.launch(Manifest.permission.RECORD_AUDIO)
                }
            },
            modifier = Modifier.fillMaxWidth().height(56.dp),
        ) { Text(if (recording) "停止" else "开始录音") }
        Button(
            onClick = {
                val data = audio ?: return@Button
                sending = true
                scope.launch {
                    val result = ConnectionManager.sendVoiceMessage(data)
                    sending = false
                    if (result.success) onBack()
                }
            },
            enabled = audio != null && !recording && !sending,
            modifier = Modifier.fillMaxWidth().height(56.dp),
        ) { Text(if (sending) "发送中…" else "发送语音") }
    }
}

private fun startRecord(
    scope: kotlinx.coroutines.CoroutineScope,
    stop: AtomicBoolean,
    onSeconds: (Int) -> Unit,
    onRecording: (Boolean) -> Unit,
    onAudio: (ByteArray) -> Unit,
) {
    stop.set(false)
    onRecording(true)
    scope.launch {
        val data = runCatching { VoiceRecorder.record(stop, onSeconds) }.getOrDefault(ByteArray(0))
        onRecording(false)
        if (data.isNotEmpty()) onAudio(data)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VolumeScreen(onBack: () -> Unit) {
    val snapshot by ConnectionManager.snapshot.collectAsState()
    var level by remember(snapshot?.volumePercent) { mutableFloatStateOf((snapshot?.volumePercent ?: 0).toFloat()) }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("音量") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (snapshot?.isVolumeControlAvailable != true) {
                PermissionHint("当前没有可控制的播放设备。")
            } else {
                Text(if (snapshot?.isMuted == true) "已静音 · ${level.toInt()}%" else "当前音量 ${level.toInt()}%")
                Slider(value = level, onValueChange = { level = it }, valueRange = 0f..100f, onValueChangeFinished = { ConnectionManager.setVolume(level.toInt()) })
                Button(onClick = { ConnectionManager.setMuted(!(snapshot?.isMuted ?: false)) }, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                    Text(if (snapshot?.isMuted == true) "取消静音" else "静音")
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PowerScreen(onBack: () -> Unit) {
    var pending by remember { mutableStateOf<Pair<Int, String>?>(null) }
    val snapshot by ConnectionManager.snapshot.collectAsState()
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("电源控制") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(
                Protocol.POWER_SHUTDOWN to "关机",
                Protocol.POWER_RESTART to "重启",
                Protocol.POWER_SLEEP to "睡眠",
                Protocol.POWER_HIBERNATE to "休眠",
            ).forEach { (action, label) ->
                val enabled = when (action) {
                    Protocol.POWER_SLEEP -> snapshot?.isSleepAvailable == true
                    Protocol.POWER_HIBERNATE -> snapshot?.isHibernateAvailable == true
                    else -> true
                }
                Button(onClick = { pending = action to label }, enabled = enabled, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text(label) }
            }
        }
    }
    pending?.let { (action, label) ->
        AlertDialog(
            onDismissRequest = { pending = null },
            title = { Text("确认$label") },
            text = { Text("将向教室电脑发送“$label”。请确认现场情况后再继续。") },
            confirmButton = { TextButton({ ConnectionManager.sendPowerAction(action); pending = null }) { Text("确认") } },
            dismissButton = { TextButton({ pending = null }) { Text("取消") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainMenuScreen(onBack: () -> Unit) {
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val visible = snapshot?.isMainMenuVisible == true
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("主界面") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(if (visible) "ClassIsland 主界面当前可见。" else "ClassIsland 主界面当前已隐藏。")
            Button(onClick = { ConnectionManager.setMainMenuVisible(!visible) }, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                Text(if (visible) "隐藏主界面" else "显示主界面")
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExtensionsScreen(onBack: () -> Unit) {
    val user by ConnectionManager.currentUser.collectAsState()
    val extensions by ConnectionManager.extensions.collectAsState()
    val scope = rememberCoroutineScope()
    var policies by remember { mutableStateOf<List<com.remoteci.mobile.data.ExtensionPolicyItem>>(emptyList()) }
    var selected by remember { mutableStateOf<ExtensionDefinition?>(null) }
    var args by remember { mutableStateOf<Map<String, String?>>(emptyMap()) }
    androidx.compose.runtime.LaunchedEffect(user) {
        policies = runCatching { AdminApi.extensions() }.getOrDefault(emptyList())
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("扩展功能") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (extensions.isEmpty()) {
            EmptyState("没有扩展", "其他 ClassIsland 插件注册后会出现在这里。")
        } else {
            ConnectedListCard(Modifier.padding(16.dp)) {
                extensions.filter { user?.showsOnWatch(it) == true || user?.isAdmin == true }.forEachIndexed { index, item ->
                    val visible = extensions.filter { user?.showsOnWatch(it) == true || user?.isAdmin == true }
                    AppListItem(
                        title = item.displayName,
                        supporting = item.id,
                        leading = Icons.Rounded.Extension,
                        trailing = Icons.Rounded.ChevronRight,
                        index = index,
                        count = visible.size,
                        onClick = {
                            selected = item
                            args = item.parameters.associate { it.key to it.defaultValue }
                        },
                    )
                }
            }
        }
        if (user?.isAdmin == true) {
            Text("管理策略", modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.titleMedium)
            policies.forEach { policy ->
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                    Column(Modifier.weight(1f)) { Text(policy.displayName.ifBlank { policy.id }); Text("允许普通账号：${policy.allowNonAdmin}", style = MaterialTheme.typography.bodySmall) }
                    Switch(policy.enabled, {
                        scope.launch {
                            runCatching { AdminApi.updateExtensionPolicy(policy.id, ExtensionPolicyUpdate(enabled = it, allowNonAdmin = policy.allowNonAdmin, showOnWatch = policy.showOnWatch)) }
                            policies = runCatching { AdminApi.extensions() }.getOrDefault(policies)
                        }
                    })
                }
            }
        }
    }
    selected?.let { extension ->
        AlertDialog(
            onDismissRequest = { selected = null },
            title = { Text(extension.displayName) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    extension.parameters.forEach { parameter ->
                        OutlinedTextField(
                            value = args[parameter.key].orEmpty(),
                            onValueChange = { args = args + (parameter.key to it) },
                            label = { Text(parameter.label) },
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
            },
            confirmButton = { TextButton({ ConnectionManager.runExtension(extension, args); selected = null }) { Text("运行") } },
            dismissButton = { TextButton({ selected = null }) { Text("取消") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun InboxScreen(onBack: () -> Unit) {
    var events by remember { mutableStateOf(listOf<ClassEvent>()) }
    androidx.compose.runtime.LaunchedEffect(Unit) {
        ConnectionManager.events.collect { event -> events = (listOf(event) + events).take(50) }
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("通知") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (events.isEmpty()) EmptyState("暂无通知", "上课、下课、课表变更和自定义提醒会显示在这里。")
        else ConnectedListCard(Modifier.padding(16.dp)) {
            events.forEachIndexed { index, event ->
                AppListItem(
                    title = event.message ?: event.subject ?: "RemoteCI 通知",
                    supporting = event.occurredAt,
                    leading = Icons.Rounded.Notifications,
                    index = index,
                    count = events.size,
                )
            }
        }
    }
}
