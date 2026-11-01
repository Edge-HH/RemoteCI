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
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.DesktopWindows
import androidx.compose.material.icons.rounded.Extension
import androidx.compose.material.icons.rounded.Mic
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.NotificationsActive
import androidx.compose.material.icons.rounded.NotificationsOff
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VolumeUp
import androidx.compose.material3.AlertDialog
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ClassEvent
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.NotificationRequest
import com.remoteci.mobile.data.ExtensionDefinition
import com.remoteci.mobile.data.ExtensionParameter
import com.remoteci.mobile.data.initialExtensionArgs
import com.remoteci.mobile.data.optionLabel
import com.remoteci.mobile.data.validateExtensionArgs
import com.remoteci.mobile.data.ExtensionPolicyUpdate
import com.remoteci.mobile.data.effectiveClassPermissions
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile
import com.remoteci.mobile.data.VoiceRecorder
import com.remoteci.mobile.data.WatchSettings
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.launch

/** 当前班级的权限与插件能力；控制页和扩展列表共用同一份判断。 */
private data class ControlAccess(val classPermissions: Int, val refined: Boolean, val capabilities: Set<String>)

@Composable
private fun rememberControlAccess(): ControlAccess {
    val user by ConnectionManager.currentUser.collectAsState()
    val classes by ConnectionManager.classes.collectAsState()
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val caps by ConnectionManager.availableCapabilities.collectAsState()
    val classPermissions = effectiveClassPermissions(classes, currentClassId, user?.permissions ?: 0)
    val refined = user?.usesRefinedPermissions == true || Protocol.hasRefinedBits(classPermissions)
    return ControlAccess(classPermissions, refined, caps)
}

/** 当前班级可用的控制项，加上账号可见的扩展功能；权限按当前班级计算，插件能力随插件上下线变化。 */
@Composable
internal fun rememberVisibleControlEntries(): List<ControlEntry> {
    val access = rememberControlAccess()
    val user by ConnectionManager.currentUser.collectAsState()
    val extensions by ConnectionManager.extensions.collectAsState()
    val items = visibleControlItems(access.classPermissions, access.refined, access.capabilities)
    val visibleExtensions = extensions.filter { extensionVisibleForClass(user, access.classPermissions, it) }
    return remember(items, visibleExtensions) { controlEntries(items, visibleExtensions) }
}

/** 控制页与“今天”页共用的布局：按本机保存的自定义顺序排好，常用项作为磁贴。 */
@Composable
internal fun rememberControlLayout(settings: WatchSettings): ControlLayout {
    val entries = rememberVisibleControlEntries()
    return remember(entries, settings.controlOrder, settings.controlFavorites) {
        controlLayout(entries, settings.controlOrder, settings.controlFavorites)
    }
}

internal val ControlItem.icon: ImageVector
    get() = when (this) {
        ControlItem.Notify -> Icons.Rounded.NotificationsActive
        ControlItem.Voice -> Icons.Rounded.Mic
        ControlItem.ClearNotifications -> Icons.Rounded.NotificationsOff
        ControlItem.MainMenu -> Icons.Rounded.Visibility
        ControlItem.Screenshot -> Icons.Rounded.DesktopWindows
        ControlItem.Volume -> Icons.Rounded.VolumeUp
        ControlItem.Power -> Icons.Rounded.PowerSettingsNew
        ControlItem.Extensions -> Icons.Rounded.Extension
    }

internal val ControlEntry.icon: ImageVector
    get() = when (this) {
        is ControlEntry.Builtin -> item.icon
        // 具体扩展功能沿用扩展页的通用图标。
        is ControlEntry.Extension -> Icons.Rounded.Extension
    }

/** 磁贴上的一行状态：主界面显示当前显隐状态，扩展显示注册 id，其余用简短说明。 */
internal fun ControlEntry.tileSupporting(mainMenuVisible: Boolean): String = when (this) {
    is ControlEntry.Builtin -> item.tileSupporting(mainMenuVisible)
    is ControlEntry.Extension -> id
}

/** 列表行的一行说明：内置项用默认说明，扩展功能显示注册 id。 */
internal val ControlEntry.supportingText: String
    get() = when (this) {
        is ControlEntry.Builtin -> item.supporting
        is ControlEntry.Extension -> id
    }

/** 磁贴上的一行状态：主界面显示当前显隐状态，其余用简短说明。 */
internal fun ControlItem.tileSupporting(mainMenuVisible: Boolean): String = when (this) {
    ControlItem.Notify -> "文字提醒"
    ControlItem.Voice -> "最长 60 秒"
    ControlItem.ClearNotifications -> "一键清除"
    ControlItem.MainMenu -> if (mainMenuVisible) "显示中 · 点击隐藏" else "已隐藏 · 点击显示"
    ControlItem.Screenshot -> "查看大屏"
    else -> supporting
}

/** 执行控制项：一键操作直接发命令（结果由全局 snackbar 提示），其余进入对应页面。 */
internal fun runControl(item: ControlItem, mainMenuVisible: Boolean, onOpen: (Screen) -> Unit) {
    when (item) {
        ControlItem.Notify -> onOpen(Screen.Notify)
        ControlItem.Voice -> onOpen(Screen.Voice)
        ControlItem.ClearNotifications -> ConnectionManager.clearNotifications()
        ControlItem.MainMenu -> ConnectionManager.setMainMenuVisible(!mainMenuVisible)
        ControlItem.Screenshot -> onOpen(Screen.Screenshot)
        ControlItem.Volume -> onOpen(Screen.Volume)
        ControlItem.Power -> onOpen(Screen.Power)
        ControlItem.Extensions -> onOpen(Screen.Extensions())
    }
}

/**
 * 执行控制页的一项。扩展功能的参数默认值可用时直接运行（常用磁贴就是一键操作），
 * 需要填写参数时进入扩展页的对应表单。
 */
internal fun runControl(entry: ControlEntry, mainMenuVisible: Boolean, onOpen: (Screen) -> Unit) {
    when (entry) {
        is ControlEntry.Builtin -> runControl(entry.item, mainMenuVisible, onOpen)
        is ControlEntry.Extension -> {
            val definition = entry.definition
            val args = initialExtensionArgs(definition.parameters)
            if (validateExtensionArgs(definition.parameters, args) == null) {
                ConnectionManager.runExtension(definition, args)
            } else {
                onOpen(Screen.Extensions(definition.id))
            }
        }
    }
}

/** 底栏“控制”：顶部是自定义的常用磁贴，下方按分组列出其余控制项。 */
@Composable
fun ControlListScreen(settings: WatchSettings, snackbar: androidx.compose.material3.SnackbarHostState, onOpen: (Screen) -> Unit) {
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val mainMenuVisible = snapshot?.isMainMenuVisible == true
    val layout = rememberControlLayout(settings)
    Column(Modifier.fillMaxSize()) {
        HomeTopBar(
            "课堂控制", snackbar, onOpen,
            actions = {
                // 顺序与常用项都可自定义，入口放在顶栏，不占用控制项本身的位置。
                if (!layout.isEmpty) {
                    IconButton({ onOpen(Screen.ControlLayout) }) {
                        Icon(Icons.Rounded.SwapVert, contentDescription = "自定义控制页")
                    }
                }
            },
        )
        if (layout.isEmpty) {
            EmptyState("没有可用的控制项", "这取决于账号权限，以及当前插件是否在线。")
            Spacer(Modifier.weight(1f))
        } else {
            // 控制项多于一屏（小屏或长说明换行）时必须可滚动，否则底部的“扩展功能”等入口无法触达。
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(bottom = 16.dp)) {
                if (layout.favorites.isNotEmpty()) {
                    ListSectionLabel("常用")
                    QuickActionGrid(layout.favorites, Modifier.padding(horizontal = 16.dp)) { item, modifier ->
                        QuickActionTile(
                            title = item.title,
                            icon = item.icon,
                            supporting = item.tileSupporting(mainMenuVisible),
                            modifier = modifier,
                            onClick = { runControl(item, mainMenuVisible, onOpen) },
                        )
                    }
                }
                layout.groups.forEach { group ->
                    ListSectionLabel(group.name)
                    ConnectedListCard(Modifier.padding(horizontal = 16.dp)) {
                        group.items.forEachIndexed { index, entry ->
                            AppListItem(
                                title = entry.title,
                                supporting = entry.supportingText,
                                leading = entry.icon,
                                trailing = Icons.Rounded.ChevronRight,
                                index = index,
                                count = group.items.size,
                                onClick = { runControl(entry, mainMenuVisible, onOpen) },
                            )
                        }
                    }
                }
            }
        }
        WebUiHint("更多控制操作请使用", "/Control", snackbar)
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
    var topmost by remember { mutableStateOf(false) }
    var rolling by remember { mutableStateOf(false) }
    var durationText by remember { mutableStateOf(NotificationRequest.DEFAULT_DURATION_SECONDS.toString()) }
    var repeatText by remember { mutableStateOf("1") }
    // 与 WebUI 表单的 min/max 一致：持续时间 1–3600 秒，重复 1–10 次。
    val duration = durationText.toIntOrNull()?.takeIf { it in 1..NotificationRequest.MAX_DURATION_SECONDS }
    val repeats = repeatText.toIntOrNull()?.takeIf { it in 1..NotificationRequest.MAX_REPEAT_COUNTS }
    val suggestRolling = NotificationRequest.shouldSuggestRolling(message, rolling)
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("发送通知") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            // 与服务端协议上限一致：标题 60 字、正文 500 字。
            OutlinedTextField(title, { title = it.take(NotificationRequest.MAX_TITLE_LENGTH) }, label = { Text("标题") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(
                message,
                { message = it.take(NotificationRequest.MAX_MESSAGE_LENGTH) },
                label = { Text("正文") },
                supportingText = if (suggestRolling) {
                    { Text("正文超过 ${NotificationRequest.ROLLING_SUGGESTION_THRESHOLD} 字，建议开启“正文滚动显示”，否则可能显示不全。") }
                } else null,
                minLines = 5,
                modifier = Modifier.fillMaxWidth(),
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedTextField(
                    durationText,
                    { durationText = it.filter(Char::isDigit).take(4) },
                    label = { Text("持续时间（秒）") },
                    isError = duration == null,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f),
                )
                OutlinedTextField(
                    repeatText,
                    { repeatText = it.filter(Char::isDigit).take(2) },
                    label = { Text("重复次数") },
                    isError = repeats == null,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f),
                )
            }
            Text(
                if (rolling) "正文横向滚动指定遍数，总显示时长为“持续时间 × 重复次数”。"
                else "整条提醒依次显示指定次数，每次显示“持续时间”秒。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) { Text("正文滚动显示"); Switch(rolling, { rolling = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) { Text("强调特效"); Switch(effect, { effect = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) { Text("通知音效"); Switch(sound, { sound = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) { Text("语音朗读"); Switch(speech, { speech = it }) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) { Text("提醒时置顶主界面"); Switch(topmost, { topmost = it }) }
            Button(
                onClick = {
                    ConnectionManager.sendNotification(
                        title, message, effect, sound, speech,
                        isNotificationTopmostEnabled = topmost,
                        durationSeconds = duration,
                        repeatCounts = repeats,
                        isRollingEnabled = rolling,
                    )
                },
                modifier = Modifier.fillMaxWidth().height(56.dp),
                enabled = (title.isNotBlank() || message.isNotBlank()) && duration != null && repeats != null,
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
    var forceComplete by remember { mutableStateOf(false) }
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
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("强制播放完成")
                Text("播完前课表端不能暂停或关闭语音浮窗", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Switch(forceComplete, { forceComplete = it }, enabled = !sending)
        }
        Button(
            onClick = {
                val data = audio ?: return@Button
                sending = true
                scope.launch {
                    val result = ConnectionManager.sendVoiceMessage(data, forceComplete)
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
fun ExtensionsScreen(openId: String?, onBack: () -> Unit) {
    val user by ConnectionManager.currentUser.collectAsState()
    val extensions by ConnectionManager.extensions.collectAsState()
    val access = rememberControlAccess()
    val visibleExtensions = extensions.filter { extensionVisibleForClass(user, access.classPermissions, it) }
    val scope = rememberCoroutineScope()
    var policies by remember { mutableStateOf<List<com.remoteci.mobile.data.ExtensionPolicyItem>>(emptyList()) }
    var selected by remember { mutableStateOf<ExtensionDefinition?>(null) }
    var args by remember { mutableStateOf<Map<String, String?>>(emptyMap()) }
    androidx.compose.runtime.LaunchedEffect(user) {
        policies = runCatching { AdminApi.extensions(ConnectionManager.currentClassId.value) }.getOrDefault(emptyList())
    }
    // 从控制页的扩展磁贴进入时，直接打开该扩展的参数表单。
    androidx.compose.runtime.LaunchedEffect(openId, visibleExtensions) {
        if (selected != null) return@LaunchedEffect
        visibleExtensions.firstOrNull { it.id == openId }?.let {
            selected = it
            args = initialExtensionArgs(it.parameters)
        }
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("扩展功能") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (visibleExtensions.isEmpty()) {
            EmptyState("没有扩展", "其他 ClassIsland 插件注册后会出现在这里。")
        } else {
            ConnectedListCard(Modifier.padding(16.dp)) {
                visibleExtensions.forEachIndexed { index, item ->
                    AppListItem(
                        title = item.displayName,
                        supporting = item.id,
                        leading = Icons.Rounded.Extension,
                        trailing = Icons.Rounded.ChevronRight,
                        index = index,
                        count = visibleExtensions.size,
                        onClick = {
                            selected = item
                            args = initialExtensionArgs(item.parameters)
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
                            policies = runCatching { AdminApi.extensions(ConnectionManager.currentClassId.value) }.getOrDefault(policies)
                        }
                    })
                }
            }
        }
    }
    selected?.let { extension ->
        val error = validateExtensionArgs(extension.parameters, args)
        AlertDialog(
            onDismissRequest = { selected = null },
            title = { Text(extension.displayName) },
            text = {
                Column(
                    Modifier.verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    extension.parameters.forEach { parameter ->
                        ExtensionParameterField(parameter, args[parameter.key]) { value ->
                            args = args + (parameter.key to value)
                        }
                    }
                    if (extension.parameters.isNotEmpty() && error != null) {
                        Text(error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                    }
                }
            },
            confirmButton = {
                TextButton({ ConnectionManager.runExtension(extension, args); selected = null }, enabled = error == null) { Text("运行") }
            },
            dismissButton = { TextButton({ selected = null }) { Text("取消") } },
        )
    }
}

/** 按参数类型渲染：开关用 Switch，选项用候选项显示名，数字用数字键盘，多行文本用多行输入框。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ExtensionParameterField(parameter: ExtensionParameter, value: String?, onChange: (String?) -> Unit) {
    val label = parameter.label.ifBlank { parameter.key } + if (parameter.required) " *" else ""
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        when (parameter.type) {
            Protocol.EXT_PARAM_SWITCH -> Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(label, Modifier.weight(1f))
                Switch(value.equals("true", ignoreCase = true), { onChange(if (it) "true" else "false") })
            }
            Protocol.EXT_PARAM_SELECT -> {
                Text(label, style = MaterialTheme.typography.labelLarge)
                parameter.options.forEachIndexed { index, option ->
                    FilterChip(
                        selected = value == option,
                        // 非必填选项再次点击可取消选择。
                        onClick = { onChange(if (value == option && !parameter.required) null else option) },
                        label = { Text(parameter.optionLabel(index)) },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
            else -> OutlinedTextField(
                value = value.orEmpty(),
                onValueChange = { onChange(it) },
                label = { Text(label) },
                placeholder = parameter.placeholder?.let { hint -> { Text(hint) } },
                singleLine = parameter.type == Protocol.EXT_PARAM_NUMBER || !parameter.multiline,
                minLines = if (parameter.multiline && parameter.type == Protocol.EXT_PARAM_TEXT) 3 else 1,
                keyboardOptions = if (parameter.type == Protocol.EXT_PARAM_NUMBER)
                    KeyboardOptions(keyboardType = KeyboardType.Decimal) else KeyboardOptions.Default,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        parameter.description?.takeIf { it.isNotBlank() }?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
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
