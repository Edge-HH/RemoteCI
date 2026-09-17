package com.remoteci.mobile.ui

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.Code
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.Wifi
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
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.CompatibleUpdate
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UpdateChannel
import com.remoteci.mobile.data.UpdateManager
import com.remoteci.mobile.data.WatchSettings
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
    onLoggedOut: () -> Unit,
) {
    BackHandler(onBack = onBack)
    when (screen) {
        is Screen.Swap -> SwapScreen(screen.date, screen.index, onBack, snackbar, onOpen)
        Screen.Help -> HelpScreen(onBack)
        Screen.Account -> AccountScreen(settings, onBack, onOpen, onPersist, onLoggedOut)
        Screen.Inbox -> InboxScreen(onBack)
        Screen.Notify -> NotifyScreen(onBack)
        Screen.Voice -> VoiceScreen(onBack)
        Screen.Volume -> VolumeScreen(onBack)
        Screen.Power -> PowerScreen(onBack)
        Screen.MainMenu -> MainMenuScreen(onBack)
        Screen.Extensions -> ExtensionsScreen(onBack)
        Screen.VisitorAccess -> VisitorAccessScreen(onBack, snackbar)
        Screen.Roles -> RolesScreen(onBack, snackbar)
        Screen.Users -> UsersScreen(onBack, snackbar)
        Screen.Sessions -> SessionsScreen(onBack, snackbar)
        Screen.Pairing -> PairingScreen(onBack, snackbar)
        Screen.Connection -> ConnectionScreen(settings, onBack, onPersist)
        Screen.NotificationSettings -> NotificationSettingsScreen(settings, onBack, onPersist)
        Screen.Appearance -> AppearanceScreen(settings, onBack, onPersist)
        Screen.Updates -> UpdatesScreen(settings, onBack, onPersist)
        Screen.Developer -> DeveloperScreen(settings, onBack, onPersist)
        Screen.System -> SystemScreen(onBack, snackbar)
        Screen.Replace -> SwapScreen(null, null, onBack, snackbar, onOpen)
        else -> AccountScreen(settings, onBack, onOpen, onPersist, onLoggedOut)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountScreen(
    settings: WatchSettings,
    onBack: () -> Unit,
    onOpen: (Screen) -> Unit,
    onPersist: (WatchSettings) -> Unit,
    onLoggedOut: () -> Unit,
) {
    val user by ConnectionManager.currentUser.collectAsState()
    val connection by ConnectionManager.state.collectAsState()
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(
            title = { Text("账号与设置", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
            actions = {
                IconButton({
                    ConnectionManager.logout(settings)
                    onLoggedOut()
                }) { Icon(Icons.AutoMirrored.Rounded.Logout, contentDescription = "退出登录") }
            },
        )
        Card(
            modifier = Modifier.padding(16.dp).fillMaxWidth().height(136.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
            shape = androidx.compose.foundation.shape.RoundedCornerShape(20.dp),
            onClick = { onOpen(Screen.Connection) },
        ) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                val role = if (user?.isAdmin == true) "管理员" else (user?.username ?: "未登录")
                Text("${user?.displayName ?: settings.username} - $role", style = MaterialTheme.typography.titleMedium)
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
        val rows = listOf(
            Triple("连接与服务器", "账号、云端地址、局域网插件发现与重新连接", Screen.Connection to Icons.Rounded.Wifi),
            Triple("通知设置", "课程、自动化和第三方插件提醒的同步开关", Screen.NotificationSettings to Icons.Rounded.Notifications),
            Triple("外观", "主题与显示偏好", Screen.Appearance to Icons.Rounded.Palette),
            Triple("更新", "检查更新与同版本强制覆盖", Screen.Updates to Icons.Rounded.SystemUpdate),
            Triple("开发者设置", "云端中转、局域网连接开关与重新连接", Screen.Developer to Icons.Rounded.Code),
            Triple("系统配置", "服务端更新与备份", Screen.System to Icons.Rounded.Settings),
        )
        ConnectedListCard(Modifier.padding(horizontal = 16.dp)) {
            rows.forEachIndexed { index, (title, supporting, dest) ->
                val (screen, icon) = dest
                AppListItem(
                    title = title,
                    supporting = supporting,
                    leading = icon,
                    trailing = Icons.Rounded.ChevronRight,
                    index = index,
                    count = rows.size,
                    onClick = { onOpen(screen) },
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    val scope = rememberCoroutineScope()
    var server by remember { mutableStateOf(settings.cloudServerUrl) }
    var username by remember { mutableStateOf(settings.username) }
    val scanStatus by ConnectionManager.lanDiscoveryStatus.collectAsState()
    val plugins by ConnectionManager.lanPlugins.collectAsState()
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        result.contents?.let { server = it }
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("连接与服务器") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(server, { server = it }, label = { Text("服务器地址") }, modifier = Modifier.fillMaxWidth(), trailingIcon = {
                IconButton({ scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("扫描服务器地址")) }) {
                    Icon(Icons.Rounded.QrCodeScanner, contentDescription = "扫描")
                }
            })
            OutlinedTextField(username, { username = it }, label = { Text("ID") }, modifier = Modifier.fillMaxWidth())
            Button(onClick = {
                val next = settings.copy(cloudServerUrl = server.trim(), username = username.trim())
                onPersist(next)
                ConnectionManager.connect(next)
            }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("保存并重新连接") }
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
                MobilePalette.All.forEach { palette ->
                    AppearanceChoice(
                        title = palette.label,
                        supporting = "与 Wear OS ${palette.label}方案一致",
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
    supporting: String,
    selected: Boolean,
    swatch: androidx.compose.ui.graphics.Color? = null,
    onClick: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = { Text(supporting) },
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
        TopAppBar(title = { Text("更新") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = settings.updateChannel == UpdateChannel.STABLE, onClick = { onPersist(settings.copy(updateChannel = UpdateChannel.STABLE)) }, label = { Text("正式版") })
            FilterChip(selected = settings.updateChannel == UpdateChannel.BETA, onClick = { onPersist(settings.copy(updateChannel = UpdateChannel.BETA)) }, label = { Text("Beta") })
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
fun DeveloperScreen(settings: WatchSettings, onBack: () -> Unit, onPersist: (WatchSettings) -> Unit) {
    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("开发者设置") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("关闭云端后只走局域网直连；关闭局域网则只使用云端。重新登录时仍使用云端账号认证。")
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("云服务器")
                Switch(settings.cloudConnectionEnabled, { onPersist(settings.copy(cloudConnectionEnabled = it)) })
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("局域网直连")
                Switch(settings.lanConnectionEnabled, { onPersist(settings.copy(lanConnectionEnabled = it)) })
            }
            Button(onClick = { ConnectionManager.connect(settings) }, modifier = Modifier.fillMaxWidth().height(56.dp)) { Text("应用并重连") }
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
        TopAppBar(title = { Text("系统配置") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
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
                    AppListItem(
                        title = backup.name,
                        supporting = "${backup.createdAt ?: ""} · ${backup.size} 字节",
                        leading = Icons.Rounded.Settings,
                        trailingText = "恢复",
                        onClick = {
                            scope.launch {
                                runCatching { AdminApi.restoreBackup(backup.name) }
                                    .onSuccess { snackbar.showSnackbar("已请求恢复") }
                                    .onFailure { snackbar.showSnackbar(it.message ?: "恢复失败") }
                            }
                        },
                    )
                }
            }
        }
    }
}
