package com.remoteci.mobile.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Spacer
import androidx.compose.material.icons.rounded.AdminPanelSettings
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.PersonOutline
import androidx.compose.material3.OutlinedButton
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.CalendarMonth
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.Code
import androidx.compose.material.icons.rounded.Key
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.journeyapps.barcodescanner.ScanContract
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.CompatibleUpdate
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.canAutoFollowTeachingClass
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UpdateChannel
import com.remoteci.mobile.data.UpdateManager
import com.remoteci.mobile.data.UserProfile
import com.remoteci.mobile.data.WatchSettings
import com.remoteci.mobile.data.normalizeServerUrl
import com.remoteci.mobile.data.parseLoginQrPayload
import kotlinx.coroutines.launch

@Composable
fun SecondaryHost(
    screen: Screen,
    settings: WatchSettings,
    snackbar: SnackbarHostState,
    onBack: () -> Unit,
    onOpen: (Screen) -> Unit,
    onHome: (HomeTab) -> Unit,
    onPersist: (WatchSettings) -> Unit,
) {
    BackHandler(onBack = onBack)
    when (screen) {
        is Screen.Swap -> SwapScreen(screen.date, screen.index, onBack, snackbar, onOpen)
        Screen.Help -> HelpScreen(onBack)
        Screen.Inbox -> InboxScreen(onBack)
        Screen.Notify -> NotifyScreen(onBack)
        Screen.Voice -> VoiceScreen(onBack)
        Screen.Volume -> VolumeScreen(onBack)
        Screen.Screenshot -> ScreenshotScreen(onBack)
        Screen.Power -> PowerScreen(onBack)
        Screen.MainMenu -> MainMenuScreen(onBack)
        Screen.Extensions -> ExtensionsScreen(onBack)
        Screen.VisitorAccess -> VisitorAccessScreen(onBack, snackbar)
        Screen.Roles -> RolesScreen(onBack, snackbar)
        Screen.Users -> UsersScreen(onBack, snackbar)
        Screen.Sessions -> SessionsScreen(onBack, snackbar)
        Screen.Pairing -> PairingScreen(onBack, snackbar)
        Screen.Connection -> ConnectionScreen(settings, onBack, onPersist, snackbar)
        Screen.NotificationSettings -> NotificationSettingsScreen(settings, onBack, onPersist)
        Screen.CalendarSync -> CalendarSyncScreen(onBack, snackbar)
        Screen.ScheduleSettings -> ScheduleSettingsScreen(onBack)
        Screen.Appearance -> AppearanceScreen(settings, onBack, onPersist)
        Screen.Updates -> UpdatesScreen(settings, onBack, onPersist)
        Screen.System -> SystemScreen(onBack, snackbar)
        Screen.Replace -> SwapScreen(null, null, onBack, snackbar, onOpen)
        // 登录、班级选择与首页由 RemoteCiApp 直接渲染，不会进入这里。
        else -> Unit
    }
}

/** “我的”页一行设置：标题、说明、目标页面与图标。 */
private data class SettingsRow(val title: String, val supporting: String, val screen: Screen, val icon: ImageVector)

/** 带分组标题的一组设置行。 */
@Composable
private fun SettingsGroup(label: String, rows: List<SettingsRow>, onOpen: (Screen) -> Unit) {
    if (rows.isEmpty()) return
    ListSectionLabel(label)
    ConnectedListCard(Modifier.padding(horizontal = 16.dp)) {
        rows.forEachIndexed { index, row ->
            AppListItem(
                title = row.title,
                supporting = row.supporting,
                leading = row.icon,
                trailing = Icons.Rounded.ChevronRight,
                index = index,
                count = rows.size,
                onClick = { onOpen(row.screen) },
            )
        }
    }
}

/**
 * 底栏“我的”：账号与连接状态、当前班级，以及按“我的偏好 / 账号与连接 / 管理 / 关于”分组的设置。
 * 管理分组只列出当前账号有权限的项；退出登录放在列表最后。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountScreen(
    settings: WatchSettings,
    onOpen: (Screen) -> Unit,
    onPersist: (WatchSettings) -> Unit,
    onLoggedOut: () -> Unit,
) {
    val user by ConnectionManager.currentUser.collectAsState()
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val connection by ConnectionManager.state.collectAsState()
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("我的", style = MaterialTheme.typography.titleLarge) })
        Card(
            modifier = Modifier.padding(16.dp).fillMaxWidth().height(136.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
            shape = androidx.compose.foundation.shape.RoundedCornerShape(20.dp),
            onClick = { onOpen(Screen.Connection) },
        ) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("${user?.displayName ?: settings.username} · ${user?.roleLabel ?: "未登录"}", style = MaterialTheme.typography.titleMedium)
                Text(
                    when (val currentConnection = connection) {
                        ConnectionManager.State.CloudConnected -> "已连接到 RemoteCI 服务器（云端）"
                        ConnectionManager.State.LanConnected -> "已连接到 RemoteCI 服务器（局域网）"
                        ConnectionManager.State.Connecting -> "正在连接…"
                        is ConnectionManager.State.Error -> currentConnection.message
                        else -> "点击切换账号或连接其他服务器"
                    },
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
        }
        // 多班级账号在这里换班；首页不再放班级切换行，避免顶栏被挤到状态栏下方。
        val classes by ConnectionManager.classes.collectAsState()
        if (classes.size > 1) {
            var pickingClass by remember { mutableStateOf(false) }
            val currentClass = classes.firstOrNull { it.id == currentClassId }
            Card(
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp).fillMaxWidth(),
                onClick = { pickingClass = true },
            ) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("当前班级", style = MaterialTheme.typography.titleSmall)
                    Text(
                        currentClass?.name ?: "未选择 · 点击切换班级",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (pickingClass) ClassSwitchDialog(classes, currentClassId, onDismiss = { pickingClass = false })
            // 只有按显示名绑定课表的老师与班主任才有跨班级的个人日程可跟随。
            if (canAutoFollowTeachingClass(user, classes)) {
                Card(modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp).fillMaxWidth()) {
                    Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text("上课时自动切换到任教班级", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "按我的日程在上课时或课前 10 分钟切到所在班级；手动换班后，当前课结束前不再自动切回",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Switch(settings.autoFollowTeachingClass, { onPersist(settings.copy(autoFollowTeachingClass = it)) })
                    }
                }
            }
        }
        SettingsGroup(
            "我的偏好",
            listOf(
                SettingsRow("通知设置", "课程、自动化和第三方插件提醒的同步开关", Screen.NotificationSettings, Icons.Rounded.Notifications),
                SettingsRow("系统日历", "把我的日程或班级课表同步到系统日历，由日历应用提醒", Screen.CalendarSync, Icons.Rounded.CalendarMonth),
                SettingsRow("外观", "主题与显示偏好", Screen.Appearance, Icons.Rounded.Palette),
            ),
            onOpen,
        )
        SettingsGroup(
            "账号与连接",
            listOf(
                SettingsRow("密码与设备会话", "修改密码，查看并吊销已登录的手机和手表", Screen.Sessions, Icons.Rounded.Key),
                SettingsRow("服务器与连接方式", "账号、服务器地址、局域网插件发现，以及云端与局域网连接开关", Screen.Connection, Icons.Rounded.Wifi),
            ),
            onOpen,
        )
        SettingsGroup(
            "管理",
            visibleManagementItems(user).map { item ->
                when (item) {
                    ManagementItem.Users -> SettingsRow(item.title, item.supporting, Screen.Users, Icons.Rounded.Group)
                    ManagementItem.Roles -> SettingsRow(item.title, item.supporting, Screen.Roles, Icons.Rounded.AdminPanelSettings)
                    ManagementItem.VisitorAccess -> SettingsRow(item.title, item.supporting, Screen.VisitorAccess, Icons.Rounded.PersonOutline)
                    ManagementItem.Pairing -> SettingsRow(item.title, item.supporting, Screen.Pairing, Icons.Rounded.Link)
                    ManagementItem.ScheduleSettings -> SettingsRow(item.title, item.supporting, Screen.ScheduleSettings, Icons.Rounded.Schedule)
                    ManagementItem.ServerMaintenance -> SettingsRow(item.title, item.supporting, Screen.System, Icons.Rounded.Settings)
                }
            },
            onOpen,
        )
        SettingsGroup(
            "关于",
            listOf(SettingsRow("应用更新", "检查更新与同版本强制覆盖", Screen.Updates, Icons.Rounded.SystemUpdate)),
            onOpen,
        )
        ConnectedListCard(Modifier.padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 16.dp)) {
            AppListItem(
                title = "退出登录",
                leading = Icons.AutoMirrored.Rounded.Logout,
                onClick = {
                    ConnectionManager.logout(settings)
                    onLoggedOut()
                },
            )
        }
    }
}

/** 与 WebUI“自动拉取课表”使用同一个管理 API；旧服务端不支持时保留当前页面并提示原因。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScheduleSettingsScreen(onBack: () -> Unit) {
    val user by ConnectionManager.currentUser.collectAsState()
    val scope = rememberCoroutineScope()
    var interval by remember { mutableStateOf(0) }
    var status by remember { mutableStateOf("正在读取设置…") }
    val choices = listOf(0 to "关闭", 15 to "每 15 分钟", 60 to "每小时", 360 to "每 6 小时", 1440 to "每天")
    LaunchedEffect(user) {
        if (user != null) {
            runCatching { AdminApi.schedulePull() }
                .onSuccess { interval = it.intervalMinutes; status = "已读取服务端设置" }
                .onFailure { status = it.message ?: "旧版服务端未提供自动拉取设置" }
        }
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("自动拉取课表") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("插件每次连接时都会拉取课表；这里设置在线期间的额外刷新周期。", style = MaterialTheme.typography.bodyMedium)
            choices.forEach { (value, label) ->
                FilterChip(
                    selected = interval == value,
                    onClick = { interval = value },
                    label = { Text(label) },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            Text(status, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Button(
                onClick = {
                    scope.launch {
                        runCatching { AdminApi.setSchedulePull(interval) }
                            .onSuccess { saved -> status = "已保存：${choices.firstOrNull { it.first == saved.intervalMinutes }?.second ?: "自定义"}" }
                            .onFailure { status = it.message ?: "保存失败" }
                    }
                },
                enabled = user?.isAdmin == true,
                modifier = Modifier.fillMaxWidth().height(56.dp),
            ) { Text("保存设置") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(
    settings: WatchSettings,
    onBack: () -> Unit,
    onPersist: (WatchSettings) -> Unit,
    snackbar: SnackbarHostState,
) {
    val scope = rememberCoroutineScope()
    var server by remember { mutableStateOf(settings.cloudServerUrl) }
    var username by remember { mutableStateOf(settings.username) }
    var password by remember { mutableStateOf("") }
    val scanStatus by ConnectionManager.lanDiscoveryStatus.collectAsState()
    val plugins by ConnectionManager.lanPlugins.collectAsState()
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents?.trim().orEmpty()
        val login = parseLoginQrPayload(contents)
        if (login != null) {
            // WebUI 扫码登录：直接切换到二维码所属的服务器与账号，无需输入密码。
            val next = settings.copy(cloudServerUrl = login.serverUrl, username = login.username)
            server = login.serverUrl
            username = login.username
            password = ""
            onPersist(next)
            ConnectionManager.connect(next, mobileLoginTicket = login.ticket)
        } else if (contents.isNotEmpty()) {
            // 旧版二维码只包含服务器地址。
            server = contents
        }
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("服务器与连接方式") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(server, { server = it }, label = { Text("服务器地址") }, modifier = Modifier.fillMaxWidth(), trailingIcon = {
                IconButton({ scanner.launch(qrScanOptions("扫描服务器地址")) }) {
                    Icon(Icons.Rounded.QrCodeScanner, contentDescription = "扫描")
                }
            })
            OutlinedTextField(username, { username = it }, label = { Text("ID / 用户名") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(
                value = password,
                onValueChange = { password = it },
                label = { Text("密码") },
                supportingText = { Text("切换账号或服务器时用于登录；尚未设置密码的账号可留空。密码不会保存") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            )
            val identityChanged = username.trim() != settings.username.trim() ||
                normalizeServerUrl(server.trim()) != normalizeServerUrl(settings.cloudServerUrl)
            Button(onClick = {
                val nextServer = server.trim()
                val nextUsername = username.trim()
                if (nextServer.isBlank() || nextUsername.isBlank()) {
                    scope.launch { snackbar.showSnackbar("请填写服务器地址和 ID") }
                    return@Button
                }
                val next = settings.copy(cloudServerUrl = nextServer, username = nextUsername)
                onPersist(next)
                // 切换账号或服务器一定重新登录（空密码用于待激活账号首登设置密码）；否则留空表示沿用已保存的会话。
                ConnectionManager.connect(next, if (identityChanged) password else password.takeIf(String::isNotEmpty))
                password = ""
            }, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                Text(if (identityChanged) "登录并切换" else "保存并重新连接")
            }
            Button(onClick = { ConnectionManager.scanLanPlugins() }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("扫描局域网插件") }
            scanStatus?.let { Text(it) }
            plugins.forEach { plugin ->
                Button(onClick = {
                    scope.launch {
                        ConnectionManager.loadLanBootstrap(settings, plugin)?.let(onPersist)
                    }
                }, modifier = Modifier.fillMaxWidth()) { Text(plugin.instanceName) }
            }
        }
        // 原“开发者设置”：云端中转与局域网直连开关，排查连接问题时才需要调整。
        ListSectionLabel("连接方式")
        Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(
                "关闭云端后只走局域网直连；关闭局域网则只使用云端。重新登录时仍使用云端账号认证。",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text("云服务器")
                Switch(settings.cloudConnectionEnabled, { onPersist(settings.copy(cloudConnectionEnabled = it)) })
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text("局域网直连")
                Switch(settings.lanConnectionEnabled, { onPersist(settings.copy(lanConnectionEnabled = it)) })
            }
            OutlinedButton(onClick = { ConnectionManager.connect(settings) }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("应用并重连") }
            Spacer(Modifier.height(16.dp))
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotificationSettingsScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    val scope = rememberCoroutineScope()
    var force by remember { mutableStateOf(true) }
    LaunchedEffect(Unit) {
        force = runCatching { AdminApi.notificationSettings().forceSenderInTitle }.getOrDefault(true)
    }
    fun toggle(update: WatchSettings.() -> WatchSettings) {
        val next = settings.update()
        onPersist(next)
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("通知设置") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(
                "上课" to settings.receiveOnClass,
                "下课" to settings.receiveOnBreaking,
                "放学" to settings.receiveAfterSchool,
                "课表变更" to settings.receiveScheduleChanged,
                "自定义消息" to settings.receiveCustom,
                "自动化" to settings.receiveAutomationNotifications,
                "第三方插件" to settings.receivePluginNotifications,
            ).forEachIndexed { index, (label, checked) ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(label)
                    Switch(checked, {
                        toggle {
                            when (index) {
                                0 -> copy(receiveOnClass = it)
                                1 -> copy(receiveOnBreaking = it)
                                2 -> copy(receiveAfterSchool = it)
                                3 -> copy(receiveScheduleChanged = it)
                                4 -> copy(receiveCustom = it)
                                5 -> copy(receiveAutomationNotifications = it)
                                else -> copy(receivePluginNotifications = it)
                            }
                        }
                    })
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("通知标题包含发送人")
                Switch(force, {
                    force = it
                    scope.launch { runCatching { AdminApi.setNotificationSettings(it) } }
                })
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppearanceScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("外观") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("显示模式", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(24.dp))) {
                listOf(
                    Triple("system", "跟随系统", "自动匹配系统的浅色或深色外观"),
                    Triple("light", "浅色", "始终使用浅色外观"),
                    Triple("dark", "深色", "始终使用深色外观"),
                ).forEach { (id, label, supporting) ->
                    AppearanceChoice(
                        title = label,
                        supporting = supporting,
                        selected = settings.appearanceMode == id,
                        onClick = { onPersist(settings.copy(appearanceMode = id)) },
                    )
                }
            }
            Text("配色方案", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(24.dp))) {
                AppearanceChoice(
                    title = "动态取色",
                    supporting = "从系统壁纸提取颜色（Android 12+）",
                    selected = settings.themeId == DynamicThemeId,
                    onClick = { onPersist(settings.copy(themeId = DynamicThemeId)) },
                )
                MobilePalette.All.forEach { palette ->
                    AppearanceChoice(
                        title = palette.label,
                        supporting = null,
                        swatch = palette.lightPrimary,
                        selected = settings.themeId == palette.id,
                        onClick = { onPersist(settings.copy(themeId = palette.id)) },
                    )
                }
            }
        }
    }
}

@Composable
private fun AppearanceChoice(
    title: String,
    supporting: String? = null,
    selected: Boolean,
    swatch: androidx.compose.ui.graphics.Color? = null,
    onClick: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = supporting?.let { value -> { Text(value) } },
        leadingContent = swatch?.let { color ->
            { Box(Modifier.size(24.dp).clip(CircleShape).background(color)) }
        },
        trailingContent = { RadioButton(selected = selected, onClick = null) },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick),
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UpdatesScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("尚未检查") }
    var update by remember { mutableStateOf<CompatibleUpdate?>(null) }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopAppBar(title = { Text("应用更新") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = settings.updateChannel == UpdateChannel.STABLE, onClick = { onPersist(settings.copy(updateChannel = UpdateChannel.STABLE)) }, label = { Text("正式版") })
            FilterChip(selected = settings.updateChannel == UpdateChannel.BETA, onClick = { onPersist(settings.copy(updateChannel = UpdateChannel.BETA)) }, label = { Text("Beta") })
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("自动检查更新")
                Text("打开 App 时检查（至少间隔 6 小时），有新版本时弹窗提示", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Switch(settings.autoUpdateCheck, { onPersist(settings.copy(autoUpdateCheck = it)) })
        }
        if (settings.skippedUpdateVersion.isNotBlank()) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text("已跳过 ${settings.skippedUpdateVersion}", Modifier.weight(1f), color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton({ onPersist(settings.copy(skippedUpdateVersion = "", lastUpdateCheckAt = 0L)) }) { Text("恢复提示") }
            }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text("同版本强制覆盖")
            Switch(settings.forceUpdateEnabled, { onPersist(settings.copy(forceUpdateEnabled = it)) })
        }
        Text(status)
        Button(onClick = {
            scope.launch {
                status = "正在检查…"
                val found = runCatching {
                    val releases = UpdateManager.fetchReleases()
                    UpdateManager.selectCompatibleUpdate(releases, com.remoteci.mobile.BuildConfig.VERSION_NAME, settings.updateChannel, settings.forceUpdateEnabled)
                }.getOrElse { status = it.message ?: "检查失败"; null }
                update = found
                status = found?.let { "发现 ${it.release.tagName}" } ?: "已是当前渠道可用的最新版本"
            }
        }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("检查更新") }
        update?.let { candidate ->
            Button(onClick = {
                scope.launch {
                    status = "正在下载…"
                    runCatching {
                        val file = UpdateManager.downloadApk(context, candidate.asset)
                        UpdateManager.installApk(context, file)
                    }.onSuccess { status = "请按系统提示完成安装" }
                        .onFailure { status = it.message ?: "安装失败" }
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("下载并安装") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SystemScreen(onBack: () -> Unit, snackbar: SnackbarHostState) {
    val user by ConnectionManager.currentUser.collectAsState()
    val scope = rememberCoroutineScope()
    var info by remember { mutableStateOf<com.remoteci.mobile.data.SystemInfo?>(null) }
    var backups by remember { mutableStateOf(listOf<com.remoteci.mobile.data.BackupFileInfo>()) }
    LaunchedEffect(user) {
        if (user?.isAdmin == true) {
            info = runCatching { AdminApi.systemInfo() }.getOrNull()
            backups = runCatching { AdminApi.backups() }.getOrDefault(emptyList())
        }
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("服务端维护") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (user?.isAdmin != true) {
            EmptyState("仅管理员可管理系统", "服务端更新和备份不会对普通账号开放。")
        } else {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("当前服务端 ${info?.currentVersion ?: "未知"}")
                info?.message?.takeIf { it.isNotBlank() }?.let { Text(it) }
                Button(onClick = {
                    scope.launch {
                        runCatching { AdminApi.checkUpdate("stable") }
                            .onSuccess { snackbar.showSnackbar(it?.tag ?: "没有可用更新") }
                            .onFailure { snackbar.showSnackbar(it.message ?: "检查失败") }
                    }
                }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = info?.canSelfUpdate == true) { Text("检查服务端更新") }
                Button(onClick = {
                    scope.launch {
                        runCatching { AdminApi.createBackup() }
                        backups = runCatching { AdminApi.backups() }.getOrDefault(backups)
                    }
                }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("立即备份") }
                if (backups.isEmpty()) EmptyState("没有备份", "创建后可在这里恢复或删除。")
                backups.forEach { backup ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                        AppListItem(
                            title = backup.name,
                            supporting = "${backup.createdAt ?: ""} · ${backup.size} 字节",
                            leading = Icons.Rounded.Settings,
                            trailingText = "恢复",
                            modifier = Modifier.weight(1f),
                            onClick = {
                                scope.launch {
                                    runCatching { AdminApi.restoreBackup(backup.name) }
                                        .onSuccess { snackbar.showSnackbar("已请求恢复") }
                                        .onFailure { snackbar.showSnackbar(it.message ?: "恢复失败") }
                                }
                            },
                        )
                        TextButton(onClick = {
                            scope.launch {
                                runCatching { AdminApi.deleteBackup(backup.name) }
                                    .onSuccess { backups = AdminApi.backups(); snackbar.showSnackbar("备份已删除") }
                                    .onFailure { snackbar.showSnackbar(it.message ?: "删除失败") }
                            }
                        }) { Text("删除") }
                    }
                }
            }
        }
    }
}
