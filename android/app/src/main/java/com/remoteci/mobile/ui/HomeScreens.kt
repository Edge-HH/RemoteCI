package com.remoteci.mobile.ui

import android.os.SystemClock
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material.icons.rounded.WarningAmber
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.VisualTransformation
import com.remoteci.mobile.calendar.CalendarFeed
import com.remoteci.mobile.data.parseWebLoginQrPayload
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.AdminPanelSettings
import androidx.compose.material.icons.rounded.CalendarMonth
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.DirectionsRun
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.Functions
import androidx.compose.material.icons.rounded.Help
import androidx.compose.material.icons.rounded.AccountCircle
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material.icons.rounded.Today
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material.icons.rounded.CalendarViewWeek
import androidx.compose.material.icons.automirrored.rounded.ViewList
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowRight
import androidx.compose.ui.graphics.luminance
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyScheduleWeekResponse
import com.remoteci.mobile.data.SchedulePeriod
import com.remoteci.mobile.data.ScheduleWeekResponse
import com.remoteci.mobile.data.SubjectColorStore
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Surface
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.Button
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.semantics.Role
import com.journeyapps.barcodescanner.ScanContract
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.MyScheduleResponse
import com.remoteci.mobile.data.MyCourseSlot
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.ScheduleChangeRequest
import com.remoteci.mobile.data.ScheduleDay
import com.remoteci.mobile.data.WatchSettings
import com.remoteci.mobile.data.classDisplayName
import com.remoteci.mobile.data.effectiveClassPermissions
import com.remoteci.mobile.data.parseLoginQrPayload
import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.format.TextStyle
import java.util.Locale
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LoginScreen(
    settings: WatchSettings,
    connection: ConnectionManager.State,
    snackbar: SnackbarHostState,
    onSettings: (WatchSettings) -> Unit,
    onLoggedIn: () -> Unit,
) {
    var username by remember { mutableStateOf(settings.username) }
    var password by remember { mutableStateOf("") }
    var passwordVisible by remember { mutableStateOf(false) }
    var server by remember { mutableStateOf(settings.cloudServerUrl) }
    val pendingSetup by ConnectionManager.pendingPasswordSetup.collectAsState()
    var newPassword by remember { mutableStateOf("") }
    var confirmNewPassword by remember { mutableStateOf("") }
    var settingUp by remember { mutableStateOf(false) }
    val plugins by ConnectionManager.lanPlugins.collectAsState()
    val scanStatus by ConnectionManager.lanDiscoveryStatus.collectAsState()
    val scanning by ConnectionManager.lanDiscoveryScanning.collectAsState()
    val pending by ConnectionManager.lanBootstrapPending.collectAsState()
    val connecting = connection is ConnectionManager.State.Connecting
    val scope = rememberCoroutineScope()
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents?.trim().orEmpty()
        val login = parseLoginQrPayload(contents)
        if (login != null) {
            // WebUI 扫码登录：二维码携带服务器地址、登录 ID 与一次性票据，直接换取设备会话。
            val next = settings.copy(cloudServerUrl = login.serverUrl, username = login.username)
            server = login.serverUrl
            username = login.username
            password = ""
            onSettings(next)
            ConnectionManager.connect(next, mobileLoginTicket = login.ticket)
        } else if (parseWebLoginQrPayload(contents) != null) {
            // 网页登录页的二维码是给已登录的 App 确认用的，不能用来登录 App 本身。
            scope.launch { snackbar.showSnackbar("这是网页登录二维码：请先登录 App，再用首页右上角的扫码按钮扫描") }
        } else if (contents.isNotEmpty()) {
            // 旧版二维码/手动输入只包含服务器地址。
            server = contents
            onSettings(settings.copy(cloudServerUrl = contents))
        }
    }
    LaunchedEffect(connection, ConnectionManager.currentUser.collectAsState().value) {
        if (connection is ConnectionManager.State.CloudConnected || connection is ConnectionManager.State.LanConnected) {
            if (ConnectionManager.currentUser.value != null) onLoggedIn()
        }
    }
    fun submit() {
        val next = settings.copy(username = username.trim(), cloudServerUrl = server.trim())
        onSettings(next)
        // 密码可留空：未设置密码的账号以空密码登录后进入首登设置密码流程。
        if (username.isBlank() || server.isBlank()) {
            scope.launch { snackbar.showSnackbar("请填写服务器和 ID") }
        } else ConnectionManager.connect(next, password)
    }
    val colors = MaterialTheme.colorScheme
    Scaffold(containerColor = colors.surface) { padding ->
        Box(
            Modifier.fillMaxSize().background(
                Brush.verticalGradient(
                    0f to colors.primaryContainer.copy(alpha = 0.65f),
                    0.45f to colors.surface,
                    1f to colors.surface,
                ),
            ),
        ) {
            Column(
                Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp, vertical = 28.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Surface(
                    modifier = Modifier.size(88.dp),
                    shape = RoundedCornerShape(28.dp),
                    color = colors.surface,
                    shadowElevation = 6.dp,
                ) {
                    Image(
                        painter = painterResource(com.remoteci.mobile.R.mipmap.ic_launcher_foreground),
                        contentDescription = null,
                        modifier = Modifier.padding(12.dp),
                    )
                }
                Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("RemoteCI", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
                    Text("课堂控制中心 · 登录你的账号", style = MaterialTheme.typography.bodyMedium, color = colors.onSurfaceVariant)
                }

                // 扫码登录：扫描 WebUI 概览页的“手机登录”二维码，免输服务器地址和密码。
                Card(
                    onClick = { scanner.launch(qrScanOptions("扫描 WebUI 的手机登录二维码")) },
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(24.dp),
                    colors = CardDefaults.cardColors(containerColor = colors.primaryContainer),
                ) {
                    Row(
                        Modifier.fillMaxWidth().padding(18.dp),
                        horizontalArrangement = Arrangement.spacedBy(16.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Surface(shape = CircleShape, color = colors.primary, modifier = Modifier.size(48.dp)) {
                            Box(contentAlignment = Alignment.Center) {
                                Icon(Icons.Rounded.QrCodeScanner, contentDescription = null, tint = colors.onPrimary)
                            }
                        }
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text("扫码登录", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, color = colors.onPrimaryContainer)
                            Text("扫描 WebUI 概览页的手机登录二维码，无需输入密码", style = MaterialTheme.typography.bodySmall, color = colors.onPrimaryContainer.copy(alpha = 0.8f))
                        }
                        Icon(Icons.Rounded.ChevronRight, contentDescription = null, tint = colors.onPrimaryContainer)
                    }
                }

                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    HorizontalDivider(Modifier.weight(1f))
                    Text("或使用账号密码", style = MaterialTheme.typography.labelMedium, color = colors.onSurfaceVariant)
                    HorizontalDivider(Modifier.weight(1f))
                }

                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(24.dp),
                    colors = CardDefaults.cardColors(containerColor = colors.surfaceContainerLow),
                ) {
                    Column(Modifier.fillMaxWidth().padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        OutlinedTextField(
                            server,
                            { server = it },
                            label = { Text("服务器地址") },
                            placeholder = { Text("https://remoteci.example.com") },
                            leadingIcon = { Icon(Icons.Rounded.Link, contentDescription = null) },
                            trailingIcon = {
                                IconButton({ scanner.launch(qrScanOptions("扫描登录二维码或服务器地址")) }) {
                                    Icon(Icons.Rounded.QrCodeScanner, contentDescription = "扫描")
                                }
                            },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                            shape = RoundedCornerShape(16.dp),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            username,
                            { username = it },
                            label = { Text("ID / 用户名") },
                            leadingIcon = { Icon(Icons.Rounded.Person, contentDescription = null) },
                            singleLine = true,
                            shape = RoundedCornerShape(16.dp),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            password,
                            { password = it },
                            label = { Text("密码") },
                            supportingText = { Text("尚未设置密码的账号可留空，登录后设置新密码") },
                            leadingIcon = { Icon(Icons.Rounded.Lock, contentDescription = null) },
                            trailingIcon = {
                                IconButton({ passwordVisible = !passwordVisible }) {
                                    Icon(
                                        if (passwordVisible) Icons.Rounded.VisibilityOff else Icons.Rounded.Visibility,
                                        contentDescription = if (passwordVisible) "隐藏密码" else "显示密码",
                                    )
                                }
                            },
                            singleLine = true,
                            visualTransformation = if (passwordVisible) VisualTransformation.None else PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                            keyboardActions = KeyboardActions(onGo = { if (!connecting) submit() }),
                            shape = RoundedCornerShape(16.dp),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        if (server.trim().startsWith("http://")) {
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Rounded.WarningAmber, contentDescription = null, tint = colors.error, modifier = Modifier.size(18.dp))
                                Text("当前是明文 HTTP，请确认网络可信。", style = MaterialTheme.typography.bodySmall, color = colors.error)
                            }
                        }
                        Button(
                            onClick = ::submit,
                            modifier = Modifier.fillMaxWidth().height(56.dp),
                            shape = RoundedCornerShape(16.dp),
                            enabled = !connecting,
                        ) {
                            if (connecting) {
                                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = colors.onPrimary)
                                Spacer(Modifier.size(10.dp))
                                Text("正在连接…")
                            } else {
                                Text("登录", style = MaterialTheme.typography.titleMedium)
                            }
                        }
                    }
                }

                if (pendingSetup != null) {
                    // 首登设置密码：批量导入的账号首次登录时在这里补设密码。
                    Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(24.dp)) {
                        Column(
                            Modifier.fillMaxWidth().padding(20.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Text("首次登录 · 设置密码", style = MaterialTheme.typography.titleMedium)
                            OutlinedTextField(
                                newPassword,
                                { newPassword = it },
                                label = { Text("新密码") },
                                modifier = Modifier.fillMaxWidth(),
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                                shape = RoundedCornerShape(16.dp),
                            )
                            OutlinedTextField(
                                confirmNewPassword,
                                { confirmNewPassword = it },
                                label = { Text("确认新密码") },
                                modifier = Modifier.fillMaxWidth(),
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                                shape = RoundedCornerShape(16.dp),
                            )
                            Button(
                                onClick = {
                                    if (newPassword.length < 8) {
                                        scope.launch { snackbar.showSnackbar("密码需为 8-128 个字符") }
                                    } else if (newPassword != confirmNewPassword) {
                                        scope.launch { snackbar.showSnackbar("两次输入的新密码不一致") }
                                    } else {
                                        settingUp = true
                                        // setupInitialPassword 成功后会清空挑战，先记下账号 ID。
                                        val setupUsername = pendingSetup!!.username
                                        scope.launch {
                                            val ok = runCatching {
                                                ConnectionManager.setupInitialPassword(newPassword)
                                            }.getOrElse { false }
                                            settingUp = false
                                            if (ok) {
                                                password = newPassword
                                                newPassword = ""
                                                confirmNewPassword = ""
                                                ConnectionManager.connect(settings.copy(username = setupUsername), password)
                                                snackbar.showSnackbar("密码已设置，正在登录")
                                            } else {
                                                snackbar.showSnackbar("设置失败，请重试或联系管理员")
                                            }
                                        }
                                    }
                                },
                                enabled = !settingUp,
                                modifier = Modifier.fillMaxWidth().height(52.dp),
                                shape = RoundedCornerShape(16.dp),
                            ) { Text(if (settingUp) "正在保存…" else "保存密码并登录") }
                        }
                    }
                }

                if (connection is ConnectionManager.State.Error) {
                    Card(
                        Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(16.dp),
                        colors = CardDefaults.cardColors(containerColor = colors.errorContainer),
                    ) {
                        Row(Modifier.fillMaxWidth().padding(14.dp), horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Rounded.WarningAmber, contentDescription = null, tint = colors.onErrorContainer)
                            Text(connection.message, color = colors.onErrorContainer, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }

                // 局域网发现：从同一网络里的教室插件读取云服务器地址，适合第一次不知道服务器地址时使用。
                OutlinedButton(
                    onClick = { ConnectionManager.scanLanPlugins() },
                    modifier = Modifier.fillMaxWidth().height(52.dp),
                    shape = RoundedCornerShape(16.dp),
                    enabled = !scanning,
                ) {
                    Icon(Icons.Rounded.Wifi, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.size(8.dp))
                    Text(if (scanning) "正在扫描…" else "从局域网插件获取服务器地址")
                }
                scanStatus?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant) }
                plugins.forEach { plugin ->
                    FilledTonalButton(onClick = {
                        scope.launch {
                            val updated = ConnectionManager.loadLanBootstrap(settings.copy(username = username.trim()), plugin)
                            if (updated != null) {
                                onSettings(updated)
                                server = updated.cloudServerUrl
                            }
                        }
                    }, modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(16.dp)) {
                        Icon(Icons.Rounded.Computer, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.size(8.dp))
                        Text("${plugin.instanceName} · ${plugin.host}")
                    }
                }
                pending?.let { (candidate, _) ->
                    Button(onClick = {
                        ConnectionManager.confirmLanBootstrap()?.let {
                            onSettings(it)
                            server = it.cloudServerUrl
                        }
                    }, modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(16.dp)) { Text("确认使用 ${candidate.instanceName} 的云服务器") }
                }
            }
        }
    }
}

/** 登录后的班级选择页：多班级账号先选一个进入，进入后可点任一页面顶栏的班级名切换。 */
@Composable
fun ClassPickerScreen(onPicked: () -> Unit) {
    val classes by ConnectionManager.classes.collectAsState()
    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            // 班级较多时超出一屏需要可滚动；内容较少时 fillMaxSize 保留的最小高度仍让列表垂直居中。
            modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
        ) {
            Text("选择进入的班级", style = MaterialTheme.typography.headlineSmall)
            Text(
                "该账号拥有多个班级的访问权限，进入后可点任一页面顶栏的班级名切换。",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            classes.forEach { classroom ->
                Card(onClick = {
                    ConnectionManager.switchClass(classroom.id)
                    onPicked()
                }) {
                    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(classroom.name, style = MaterialTheme.typography.titleMedium)
                        Text(
                            listOfNotNull(
                                classroom.groupName,
                                classroom.roleName,
                                if (classroom.visitorEnabled) "访客开放" else null,
                            ).joinToString(" · ").ifBlank { "班级" },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeShell(
    home: Screen.Home,
    settings: WatchSettings,
    snackbar: SnackbarHostState,
    onHome: (Screen.Home) -> Unit,
    onOpen: (Screen) -> Unit,
    onPersist: (WatchSettings) -> Unit,
    onLoggedOut: () -> Unit,
) {
    val tab = home.tab
    val onTab: (HomeTab) -> Unit = { onHome(Screen.Home(it)) }
    val user by ConnectionManager.currentUser.collectAsState()
    val pendingSwaps by ConnectionManager.pendingSwapCount.collectAsState()
    // 换课申请在“课表”里，待处理数挂在“课表”图标上。
    val canRequestSwap = user?.has(com.remoteci.mobile.data.Protocol.PERMISSION_REQUEST_SWAP) == true
    // 其他板块按返回键先回到“今天”，只有在“今天”再按返回才退出 App。
    BackHandler(enabled = tab != HomeTab.Today) { onTab(HomeTab.Today) }
    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        bottomBar = {
            // 悬浮的胶囊底栏：选中项整块铺主色容器（清新配色下是薄荷），与 WebUI 移动版底栏一致。
            Box(
                Modifier.fillMaxWidth().navigationBarsPadding().padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 12.dp),
            ) {
                Surface(
                    shape = RoundedCornerShape(36.dp),
                    color = MaterialTheme.colorScheme.surfaceContainerLowest,
                    shadowElevation = 10.dp,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Row(
                        Modifier.selectableGroup().height(72.dp).padding(8.dp),
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                    ) {
                        data class Item(val tab: HomeTab, val label: String, val icon: ImageVector)
                        listOf(
                            Item(HomeTab.Today, "今天", Icons.Rounded.Today),
                            Item(HomeTab.Schedule, "课表", Icons.Rounded.CalendarMonth),
                            Item(HomeTab.Control, "控制", Icons.Rounded.Tune),
                            Item(HomeTab.Me, "我的", Icons.Rounded.AccountCircle),
                        ).forEach { item ->
                            val selected = tab == item.tab
                            val colors = MaterialTheme.colorScheme
                            Column(
                                Modifier.weight(1f).fillMaxHeight()
                                    .clip(RoundedCornerShape(28.dp))
                                    .background(if (selected) colors.primaryContainer else Color.Transparent)
                                    .selectable(selected = selected, role = Role.Tab, onClick = { onTab(item.tab) }),
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.Center,
                            ) {
                                val tint = if (selected) colors.onPrimaryContainer else colors.onSurfaceVariant
                                if (item.tab == HomeTab.Schedule && canRequestSwap && pendingSwaps > 0) {
                                    BadgedBox(badge = { Badge(containerColor = Color(0xFFFBE9A8), contentColor = Color(0xFF6B4E12)) { Text(pendingSwaps.toString()) } }) {
                                        Icon(item.icon, contentDescription = null, tint = tint)
                                    }
                                } else {
                                    Icon(item.icon, contentDescription = null, tint = tint)
                                }
                                Text(
                                    item.label,
                                    style = MaterialTheme.typography.labelMedium,
                                    fontWeight = if (selected) FontWeight.ExtraBold else FontWeight.SemiBold,
                                    color = tint,
                                )
                            }
                        }
                    }
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (tab) {
                HomeTab.Today -> TodayScreen(settings, snackbar, onOpen, onTab, onSwapRequests = { onHome(Screen.Home(HomeTab.Schedule, openSwapRequests = true)) })
                HomeTab.Schedule -> ScheduleScreen(
                    settings = settings,
                    snackbar = snackbar,
                    onOpen = onOpen,
                    onPersist = onPersist,
                    openSwapRequests = home.openSwapRequests,
                )
                HomeTab.Control -> ControlListScreen(settings, snackbar = snackbar, onOpen = onOpen)
                HomeTab.Me -> AccountScreen(settings, snackbar, onOpen, onPersist, onLoggedOut)
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TodayScreen(settings: WatchSettings, snackbar: SnackbarHostState, onOpen: (Screen) -> Unit, onTab: (HomeTab) -> Unit, onSwapRequests: () -> Unit) {
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val schedule by ConnectionManager.schedule.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val personalNext by ConnectionManager.personalNextCourse.collectAsState()
    var nowTick by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    var generatedElapsed by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(snapshot?.generatedAt) { generatedElapsed = SystemClock.elapsedRealtime() }
    LaunchedEffect(Unit) { while (true) { delay(1000); nowTick = SystemClock.elapsedRealtime() } }
    val today = pluginToday(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, generatedElapsed, nowTick)
    val now = pluginLocalNow(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, generatedElapsed, nowTick)
    val day = schedule?.days?.firstOrNull { it.date == today.toString() }
    val home = homeCourseContent(snapshot)
    val teacherMode = user?.isTeacher == true
    // nowTick 每秒重组一次：已下课/已开始的课按当前时刻修正；主卡片只展示今天的课，
    // 开课前 TeacherUpcomingLeadMinutes 分钟内才显示“即将上课”，今天没课了显示放学。
    val teacher = teacherCourseState(personalNext)
    val teacherCurrent = teacher.current
    val teacherNext = teacher.laterToday
    val progress = if (teacherMode) teacherCurrent?.let { teacherProgress(it.startsAt, it.endsAt) } ?: 0f
        else if (shouldShowStateProgress(snapshot)) lessonProgress(snapshot?.currentTimeLayoutItem, now) else 0f
    val period = if (teacherMode) teacherCurrent?.course?.label ?: teacherNext?.course?.label ?: "—"
        else extractPeriod(home.timeLayoutItem) ?: snapshot?.currentTimeLayoutItem?.substringBefore(' ') ?: "—"
    // 有科目地点时显示真实地点（老师显示“班级 · 地点”）；没有时沿用时间表名称推测。
    val teacherSlot = teacherCurrent ?: teacherNext
    val room = if (teacherMode) listOfNotNull(
            teacherSlot?.className,
            teacherSlot?.course?.location?.takeIf { teacherSlot.course.personalCourseId == null },
        ).joinToString(" · ")
        else schedule?.subjects?.firstOrNull { it.name == home.subject.trim() }?.location
            ?: home.timeLayoutItem?.substringAfter(' ', missingDelimiterValue = "")?.ifBlank { snapshot?.classPlanName }.orEmpty()
    val timeRange = if (teacherMode) teacherCurrent?.let(::teacherSlotRange) ?: teacherNext?.let(::teacherSlotRange) ?: "—"
        else extractTimeRange(home.timeLayoutItem).ifBlank { "—" }
    val stateTitle = if (teacherMode) teacher.title else when (snapshot?.currentState) {
        Protocol.STATE_CLASS -> "上课"
        Protocol.STATE_BREAKING -> "课间"
        Protocol.STATE_PREPARE_CLASS -> "即将上课"
        Protocol.STATE_AFTER_SCHOOL -> "放学"
        else -> "待机"
    }
    // 快捷控制与控制页共用同一份“常用”设置和顺序，在控制页里自定义后这里同步变化。
    val controls = rememberControlLayout(settings).favorites
    val pendingSwaps by ConnectionManager.pendingSwapCount.collectAsState()
    Column(Modifier.fillMaxSize()) {
    // 连接状态改为标题右侧的云朵图标，点开看课表同步与手表状态。
    HomeTopBar("RemoteCI", snackbar, onOpen, showConnection = true)
    Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Card(
            onClick = {
                if (teacherMode) onTab(HomeTab.Schedule)
                else {
                    val index = homeQuickSwapLessonIndex(day, snapshot)
                    onOpen(Screen.Swap(day?.date, index))
                }
            },
            modifier = Modifier.fillMaxWidth().height(176.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
            shape = androidx.compose.foundation.shape.RoundedCornerShape(32.dp),
        ) {
            Box(Modifier.fillMaxSize().padding(20.dp)) {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    // 状态做成雾白小胶囊，和 WebUI 移动版首页卡片一致。
                    Surface(shape = CircleShape, color = MaterialTheme.colorScheme.surfaceContainerLowest) {
                        Text(
                            stateTitle,
                            modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                            style = MaterialTheme.typography.labelLarge,
                            fontWeight = FontWeight.ExtraBold,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    Text("$period ${room.ifBlank { "" }}".trim(), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
                }
                Row(
                    Modifier.align(Alignment.BottomStart).fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.Bottom,
                ) {
                    Text(
                        if (teacherMode) teacherCurrent?.course?.subject ?: teacherNext?.course?.subject ?: "暂无课程"
                        else if (home.isAvailable) home.subject else "暂无课程",
                        fontSize = 32.sp,
                        fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.onPrimaryContainer,
                        modifier = Modifier.weight(1f),
                    )
                    Column(horizontalAlignment = Alignment.End) {
                        Text(timeRange, fontSize = 14.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onPrimaryContainer)
                        Spacer(Modifier.height(8.dp))
                        CircularProgressIndicator(
                            progress = { progress },
                            modifier = Modifier.size(36.dp),
                            strokeWidth = 7.dp,
                            strokeCap = StrokeCap.Round,
                            color = MaterialTheme.colorScheme.primary,
                            trackColor = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.2f),
                        )
                    }
                }
            }
        }
        val next = if (teacherMode) teacher.next?.course?.subject else nextClassSubjectAfterCurrent(snapshot)
        ConnectedListCard {
            if ((teacherMode || shouldShowNextLessonSummary(snapshot?.currentState)) && !next.isNullOrBlank()) {
                AppListItem(
                    title = "下一节 · $next",
                    supporting = if (teacherMode) teacher.next?.let { slot ->
                        listOfNotNull(teacherSlotDayLabel(slot), slot.className, teacherSlotRange(slot)).joinToString(" · ")
                    }.orEmpty()
                    else listOfNotNull(extractPeriod(snapshot?.nextClassTimeLayoutItem), extractTimeRange(snapshot?.nextClassTimeLayoutItem)).joinToString(" ") +
                        (snapshot?.nextClassTimeLayoutItem?.substringAfter(' ', "")?.let { " · $it" } ?: ""),
                    leading = Icons.Rounded.DirectionsRun,
                    trailing = Icons.Rounded.SwapHoriz,
                    onClick = {
                        if (teacherMode) onTab(HomeTab.Schedule)
                        else onOpen(Screen.Swap(day?.date, nextQuickSwapLessonIndex(day, snapshot)))
                    },
                )
            } else {
                AppListItem("下一节", "目前没有下一节课程", Icons.Rounded.DirectionsRun)
            }
        }
        // 有待处理的换课申请时才提示；平时换课申请从“课表”进入，不再占首页位置。
        val canSwap = user?.has(Protocol.PERMISSION_REQUEST_SWAP) == true
        if (canSwap && pendingSwaps > 0) {
            AppListItem(
                title = "换课申请待处理",
                supporting = "有 $pendingSwaps 条换课申请需要你处理",
                leading = Icons.Rounded.SwapHoriz,
                trailing = Icons.Rounded.ChevronRight,
                onClick = onSwapRequests,
            )
        }
        // 快捷控制：与“控制”页同一份权限与能力判断，没有可用项时不显示。
        if (controls.isNotEmpty()) {
            Text("快捷控制", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(start = 4.dp, top = 4.dp))
            val mainMenuVisible = snapshot?.isMainMenuVisible == true
            QuickActionGrid(controls, columns = 2) { item, modifier ->
                QuickActionTile(
                    title = item.title,
                    icon = item.icon,
                    supporting = item.tileSupporting(mainMenuVisible),
                    modifier = modifier,
                    onClick = { runControl(item, mainMenuVisible, onOpen) },
                )
            }
        }
        Spacer(Modifier.height(16.dp))
    }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScheduleScreen(
    settings: WatchSettings,
    snackbar: SnackbarHostState,
    onOpen: (Screen) -> Unit,
    onPersist: (WatchSettings) -> Unit,
    openSwapRequests: Boolean = false,
) {
    val bundle by ConnectionManager.schedule.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val pull by ConnectionManager.schedulePullState.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val classes by ConnectionManager.classes.collectAsState()
    val afterSchool = snapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    val days = displayScheduleDays(bundle, afterSchool)
    var selected by remember { mutableIntStateOf(0) }
    if (selected >= days.size) selected = 0
    // 权限按当前班级计算；老师/自定义角色可能只在某个班级拥有换课权限。
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val classPermissions = effectiveClassPermissions(classes, currentClassId, user?.permissions ?: 0)
    // 换课是否可执行由服务端按班级权限和插件能力最终复核。
    // 能力快照可能在连接切换/插件刚上线时短暂缺失，不能因此把 Android 入口直接禁用。
    // 按档案推算的课表只供查看：修订号不对应教室端实际课表，不提供换课入口。
    val canChange = (classPermissions and Protocol.PERMISSION_MANAGE_SCHEDULE) == Protocol.PERMISSION_MANAGE_SCHEDULE &&
        bundle?.fromProfile != true
    val canPull = user?.canPullScheduleFor(currentClassId) == true
    val scope = rememberCoroutineScope()

    // 老师与班主任：显示名与课表教师名绑定后，可跨班级查看“我的日程”；null 表示尚未手动切换。
    // 老师默认进入“我的日程”，班主任默认仍看本班课表。
    var mineChoice by remember { mutableStateOf<Boolean?>(null) }
    // 委派属性无法智能转换，先取局部值再判断，避免 user 为 null 时误读。
    val mineMode = user?.let { mineChoice ?: it.isTeacher } == true
    // 换课申请是课表页的第三个分段；换课通知与今天页快捷入口直接打开它。
    val segments = visibleScheduleSegments(user?.has(Protocol.PERMISSION_REQUEST_SWAP) == true)
    var swapChoice by remember(openSwapRequests) { mutableStateOf(openSwapRequests) }
    val swapMode = swapChoice && ScheduleSegment.SwapRequests in segments
    val pendingSwaps by ConnectionManager.pendingSwapCount.collectAsState()
    var mySchedule by remember(user?.id) { mutableStateOf<MyScheduleResponse?>(null) }
    var myScheduleStatus by remember(user?.id) { mutableStateOf<String?>(null) }
    var myScheduleRefresh by remember { mutableIntStateOf(0) }
    var mySelected by remember(user?.id) { mutableIntStateOf(0) }
    var personalCourses by remember(user?.id) { mutableStateOf(emptyList<com.remoteci.mobile.data.PersonalCourseView>()) }
    var editingCourse by remember(user?.id) { mutableStateOf<com.remoteci.mobile.data.PersonalCourseView?>(null) }
    var showCourseEditor by remember(user?.id) { mutableStateOf(false) }
    var showCourseList by remember(user?.id) { mutableStateOf(false) }
    if (showCourseEditor) PersonalCourseEditor(editingCourse,
        onDismiss = { showCourseEditor = false },
        onSaved = { showCourseEditor = false; myScheduleRefresh++ })
    if (showCourseList) PersonalCourseListDialog(personalCourses,
        onDismiss = { showCourseList = false },
        onAdd = { showCourseList = false; editingCourse = null; showCourseEditor = true },
        onEdit = { showCourseList = false; editingCourse = it; showCourseEditor = true })
    LaunchedEffect(user?.id, mineMode, myScheduleRefresh) {
        if (mineMode && user != null) {
            mySchedule = null
            while (true) {
                runCatching { AdminApi.mySchedule() }
                    .onSuccess { mySchedule = it; myScheduleStatus = null }
                    .onFailure { myScheduleStatus = it.message ?: "暂时无法读取我的日程" }
                runCatching { AdminApi.personalCourses() }.onSuccess { personalCourses = it }
                kotlinx.coroutines.delay(30_000)
            }
        }
    }
    val myDays = mySchedule?.days.orEmpty()
    if (mySelected >= myDays.size) mySelected = 0

    // 学科颜色按账号同步：进入课表页与刷新时拉取，离线时用本机缓存。
    val colorState by SubjectColorStore.state.collectAsState()
    val overrides = remember(colorState) { SubjectColorStore.overrides(colorState) }
    val colorOf: (String) -> String = { SubjectColorStore.colorOf(overrides, it) }
    val dark = MaterialTheme.colorScheme.surface.luminance() < 0.5f
    LaunchedEffect(user?.id) { if (user != null) SubjectColorStore.refresh() }

    // 表格/列表视图：班级课表与我的日程各自记在本机，默认列表。表格按周浏览，数据单独按周读取。
    val tableMode = if (mineMode) settings.myScheduleView == "table" else settings.classScheduleView == "table"
    val today = LocalDate.now()
    var classWeekStart by remember { mutableStateOf(mondayOf(today)) }
    var myWeekStart by remember(user?.id) { mutableStateOf(mondayOf(today)) }
    var classWeek by remember { mutableStateOf<ScheduleWeekResponse?>(null) }
    var myWeek by remember(user?.id) { mutableStateOf<MyScheduleWeekResponse?>(null) }
    var weekStatus by remember { mutableStateOf<String?>(null) }
    var weekRefresh by remember { mutableIntStateOf(0) }
    LaunchedEffect(tableMode, mineMode, swapMode, currentClassId, classWeekStart, weekRefresh, bundle?.generatedAt) {
        if (!tableMode || mineMode || swapMode) return@LaunchedEffect
        val classId = currentClassId ?: return@LaunchedEffect
        runCatching { AdminApi.scheduleWeek(classId, classWeekStart.toString(), classWeekStart.plusDays(6).toString()) }
            .onSuccess { classWeek = it; weekStatus = null }
            .onFailure { weekStatus = it.message ?: "暂时无法读取这一周的课表" }
    }
    LaunchedEffect(tableMode, mineMode, swapMode, user?.id, myWeekStart, weekRefresh, myScheduleRefresh) {
        if (!tableMode || !mineMode || swapMode || user == null) return@LaunchedEffect
        runCatching { AdminApi.myScheduleWeek(myWeekStart.toString(), myWeekStart.plusDays(6).toString()) }
            .onSuccess { myWeek = it; weekStatus = null }
            .onFailure { weekStatus = it.message ?: "暂时无法读取这一周的日程" }
    }
    val display = ScheduleGridDisplay(
        mine = mineMode,
        showTeacher = settings.scheduleShowTeacher,
        showClass = settings.scheduleShowClass,
        showLocation = settings.scheduleShowLocation,
        weekendMode = settings.scheduleWeekendMode,
        merge = settings.scheduleMergeConsecutive,
    )

    Column(Modifier.fillMaxSize()) {
        // 班级名与换班入口在共用顶栏里；刷新只放顶栏一处，“我的日程”刷新个人日程，班级课表请求插件拉取。
        HomeTopBar("课表", snackbar, onOpen) {
            if (!swapMode) {
                IconButton(onClick = {
                    val next = if (tableMode) "list" else "table"
                    onPersist(if (mineMode) settings.copy(myScheduleView = next) else settings.copy(classScheduleView = next))
                }) {
                    Icon(
                        if (tableMode) Icons.AutoMirrored.Rounded.ViewList else Icons.Rounded.CalendarViewWeek,
                        contentDescription = if (tableMode) "切换到列表" else "切换到表格",
                    )
                }
                IconButton(onClick = { onOpen(Screen.ScheduleDisplay) }) { Icon(Icons.Rounded.Tune, contentDescription = "显示设置") }
                IconButton(
                    onClick = {
                        if (mineMode) myScheduleRefresh++ else ConnectionManager.requestSchedulePull()
                        weekRefresh++
                        scope.launch { SubjectColorStore.refresh() }
                    },
                    enabled = mineMode || tableMode || (canPull && schedulePullActionEnabled(pull)),
                ) {
                    Icon(Icons.Rounded.Refresh, contentDescription = if (mineMode) "刷新" else "拉取课表")
                }
            }
        }
        if (user != null) {
            val current = when {
                swapMode -> ScheduleSegment.SwapRequests
                mineMode -> ScheduleSegment.Mine
                else -> ScheduleSegment.Class
            }
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
                segments.forEachIndexed { index, segment ->
                    SegmentedButton(
                        selected = segment == current,
                        onClick = {
                            when (segment) {
                                ScheduleSegment.Mine -> { mineChoice = true; swapChoice = false }
                                ScheduleSegment.Class -> { mineChoice = false; swapChoice = false }
                                ScheduleSegment.SwapRequests -> swapChoice = true
                            }
                        },
                        shape = SegmentedButtonDefaults.itemShape(index, segments.size),
                        label = {
                            Text(
                                when (segment) {
                                    ScheduleSegment.Mine -> "我的日程"
                                    ScheduleSegment.Class -> "班级课表"
                                    ScheduleSegment.SwapRequests -> if (pendingSwaps > 0) "换课申请 $pendingSwaps" else "换课申请"
                                },
                                maxLines = 1,
                            )
                        },
                    )
                }
            }
        }
        if (swapMode) {
            SwapRequestsScreen(snackbar = snackbar, showTopBar = false)
        } else if (mineMode && tableMode) {
            PersonalCoursesButton(personalCourses.size) {
                if (personalCourses.isEmpty()) { editingCourse = null; showCourseEditor = true } else showCourseList = true
            }
            val week = myWeek?.takeIf { it.from == myWeekStart.toString() }
            ScheduleWeekPane(
                weekStart = myWeekStart,
                today = today,
                teachingWeek = week?.teachingWeek,
                minDate = week?.minDate?.let(::parseScheduleDate),
                maxDate = week?.maxDate?.let(::parseScheduleDate),
                status = weekStatus.takeIf { week == null },
                periods = week?.periods.orEmpty(),
                days = myGridDays(week, myWeekStart),
                display = display,
                colorOf = colorOf,
                zoom = settings.scheduleZoom,
                onZoomCommit = { onPersist(settings.copy(scheduleZoom = it)) },
                onWeek = { myWeekStart = it },
                onBlockClick = { block, date ->
                    val course = block.entry.course
                    val classId = block.entry.classId
                    when {
                        course.personalCourseId != null -> {
                            editingCourse = personalCourses.firstOrNull { it.id == course.personalCourseId }
                            if (editingCourse != null) showCourseEditor = true
                        }
                        classId != null && classes.any { it.id == classId } -> {
                            // 跳到该班课表表格的那一周。
                            if (classId != currentClassId) ConnectionManager.switchClass(classId)
                            classWeekStart = mondayOf(date)
                            mineChoice = false
                            if (settings.classScheduleView != "table") onPersist(settings.copy(classScheduleView = "table"))
                        }
                        else -> scope.launch { snackbar.showSnackbar("没有${block.entry.className ?: "该班级"}的课表访问权限") }
                    }
                },
                modifier = Modifier.weight(1f),
            )
        } else if (mineMode) {
            // 单双周课程本周可能不出现在日程里，统一从列表查看、修改和删除；列表里也能新增。
            PersonalCoursesButton(personalCourses.size) {
                if (personalCourses.isEmpty()) { editingCourse = null; showCourseEditor = true } else showCourseList = true
            }
            CalendarSyncRow(snackbar, myScheduleRefresh, CalendarFeed.Personal)
            when {
                myDays.isEmpty() -> EmptyState(
                    if (myScheduleStatus != null) "暂时无法读取我的日程" else "还没有与你关联的课程",
                    myScheduleStatus
                        ?: "请联系管理员确认你的用户名与课表中的教师名一致；绑定后这里会显示你在各班的课程。",
                )
                else -> {
                    PrimaryScrollableTabRow(selectedTabIndex = mySelected) {
                        myDays.forEachIndexed { index, day ->
                            val date = parseScheduleDate(day.date)
                            val label = if (date == null) day.date.takeLast(5) else {
                                val week = date.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.CHINA)
                                "$week ${date.dayOfMonth}"
                            }
                            Tab(selected = index == mySelected, onClick = { mySelected = index }, text = { Text(label, style = MaterialTheme.typography.titleSmall) })
                        }
                    }
                    val myDay = myDays[mySelected]
                    Column(
                        Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        myDay.items.forEach { item ->
                            // 插件从未同步过课表的班级按档案推算，可能与教室端实际课表不同，与 WebUI 一样标注出来。
                            // 自定义课程的“班级名”就是它的地点，始终显示；班级名按“显示班级”设置。
                            if (item.isPersonal || settings.scheduleShowClass) {
                                val title = if (item.source == Protocol.BUNDLE_SOURCE_PROFILE) "${item.className} · 按档案推算" else item.className
                                Text(title, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
                            }
                            ScheduleLogicGroups(item.courses, item.dividerAfterIndexes) { group ->
                                ConnectedListCard {
                                    group.forEachIndexed { index, course ->
                                        val time = listOfNotNull(course.startTime, course.endTime).joinToString("–")
                                        val (container, content) = subjectColorPair(colorOf(course.subject), dark)
                                        AppListItem(
                                            title = "${course.label} · ${course.subject}",
                                            supporting = listOfNotNull(
                                                time.ifEmpty { null },
                                                course.teacher,
                                                course.location?.takeIf { settings.scheduleShowLocation && !item.isPersonal },
                                            ).joinToString(" · "),
                                            leading = subjectIcon(course.subject),
                                            leadingContainerColor = container,
                                            leadingContentColor = content,
                                            index = index,
                                            count = group.size,
                                            trailing = if (course.personalCourseId != null) Icons.Rounded.Edit else null,
                                            onClick = course.personalCourseId?.let { id -> ({
                                                editingCourse = personalCourses.firstOrNull { it.id == id }
                                                if (editingCourse != null) showCourseEditor = true
                                            }) },
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } else if (tableMode) {
            if (bundle?.fromProfile == true) {
                Box(Modifier.padding(horizontal = 16.dp)) {
                    PermissionHint("${classDisplayName(classes, currentClassId)}教室端尚未同步过课表，以下按班级档案推算，可能与实际课表不同。")
                }
            }
            val week = classWeek?.takeIf { it.from == classWeekStart.toString() && it.classId.equals(currentClassId, ignoreCase = true) }
            ScheduleWeekPane(
                weekStart = classWeekStart,
                today = today,
                teachingWeek = week?.teachingWeek,
                minDate = week?.minDate?.let(::parseScheduleDate),
                maxDate = week?.maxDate?.let(::parseScheduleDate),
                status = weekStatus.takeIf { week == null },
                periods = week?.periods.orEmpty(),
                days = classGridDays(week, classWeekStart),
                display = display,
                colorOf = colorOf,
                zoom = settings.scheduleZoom,
                onZoomCommit = { onPersist(settings.copy(scheduleZoom = it)) },
                onWeek = { classWeekStart = it },
                onBlockClick = { block, date ->
                    if (canChange && !date.isBefore(today) && week?.source != Protocol.BUNDLE_SOURCE_PROFILE)
                        onOpen(Screen.Swap(date.toString(), block.indexes.firstOrNull() ?: block.entry.course.index))
                },
                modifier = Modifier.weight(1f),
            )
        } else {
        if (user != null && classes.isNotEmpty()) CalendarSyncRow(snackbar, 0, CalendarFeed.Class, classes.firstOrNull { it.id == currentClassId })
        if (days.isEmpty()) {
            EmptyState("还没有课表", if (canPull) "连接成功后会显示未来七日课程。可点右上角刷新按钮拉取课表。" else "连接成功后会显示未来七日课程。")
        } else {
            PrimaryScrollableTabRow(selectedTabIndex = selected.coerceAtMost(days.lastIndex)) {
                days.forEachIndexed { index, day ->
                    val date = parseScheduleDate(day.date)
                    val label = if (date == null) day.date.takeLast(5) else {
                        val week = date.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.CHINA)
                        "$week ${date.dayOfMonth}"
                    }
                    Tab(selected = index == selected, onClick = { selected = index }, text = { Text(label, style = MaterialTheme.typography.titleSmall) })
                }
            }
            val day = days[selected]
            val courses = day.courses.filter { it.enabled }
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                if (bundle?.fromProfile == true) {
                    PermissionHint("${classDisplayName(classes, currentClassId)}教室端尚未同步过课表，以下按班级档案推算，可能与实际课表不同；插件同步后自动改用实际课表，届时才能换课。")
                }
                if (courses.isEmpty()) {
                    EmptyState("这一天没有课程", if (canPull) "可点右上角刷新按钮拉取最新课表，或选择其他日期。" else "请选择其他日期。")
                } else {
                    // 档案中的分割线处分成多组，组间留出间隔。
                    ScheduleLogicGroups(courses, day.dividerAfterIndexes) { group ->
                        ConnectedListCard {
                            group.forEachIndexed { index, course ->
                                val time = listOfNotNull(course.startTime, course.endTime).joinToString("–")
                                val (container, content) = subjectColorPair(colorOf(course.subject), dark)
                                AppListItem(
                                    title = "${course.label} · ${course.subject}",
                                    supporting = listOfNotNull(
                                        time.ifEmpty { null },
                                        course.teacher?.takeIf { settings.scheduleShowTeacher },
                                        course.location?.takeIf { settings.scheduleShowLocation },
                                    ).joinToString(" · "),
                                    leading = subjectIcon(course.subject),
                                    leadingContainerColor = container,
                                    leadingContentColor = content,
                                    trailing = if (canChange) Icons.Rounded.Edit else null,
                                    index = index,
                                    count = group.size,
                                    onClick = if (canChange) ({ onOpen(Screen.Swap(day.date, course.index)) }) else null,
                                )
                            }
                        }
                    }
                }
                // 拉取课表在顶栏刷新按钮；这里只保留换课主操作。
                if (canChange) {
                    Button(
                        onClick = { onOpen(Screen.Swap(day.date, courses.firstOrNull()?.index)) },
                        modifier = Modifier.fillMaxWidth().height(56.dp),
                    ) { Text("换课") }
                }
                val pullText = when (val currentPull = pull) {
                    is ConnectionManager.SchedulePullState.Pulling -> currentPull.message
                    is ConnectionManager.SchedulePullState.Success -> currentPull.message
                    is ConnectionManager.SchedulePullState.Error -> currentPull.message
                    else -> null
                }
                pullText?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
        }
        }
    }
}

/** “我的日程”的自定义课程入口：没有课程时直接新增，否则打开课程列表查看、修改与新增。 */
@Composable
private fun PersonalCoursesButton(count: Int, onClick: () -> Unit) {
    TextButton(onClick = onClick, modifier = Modifier.padding(horizontal = 8.dp)) {
        Icon(Icons.Rounded.Edit, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.size(8.dp))
        Text(if (count == 0) "添加自定义课程" else "自定义课程（$count）")
    }
}

/** 列表按档案分割线把课程分组逐组渲染，组与组之间隔开。 */
@Composable
private fun ScheduleLogicGroups(
    courses: List<CourseEntry>,
    dividers: List<Int>?,
    content: @Composable (List<CourseEntry>) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        ScheduleGridLogic.splitAtDividers(courses, dividers).forEach { group -> content(group) }
    }
}

/** 表格视图：周标题、翻周按钮与周课表。 */
@Composable
private fun ScheduleWeekPane(
    weekStart: LocalDate,
    today: LocalDate,
    teachingWeek: Int?,
    minDate: LocalDate?,
    maxDate: LocalDate?,
    status: String?,
    periods: List<SchedulePeriod>,
    days: List<GridDay>,
    display: ScheduleGridDisplay,
    colorOf: (String) -> String,
    zoom: Float,
    onZoomCommit: (Float) -> Unit,
    onWeek: (LocalDate) -> Unit,
    onBlockClick: (GridBlock, LocalDate) -> Unit,
    modifier: Modifier = Modifier,
) {
    fun allowed(target: LocalDate) =
        (minDate == null || !target.plusDays(6).isBefore(minDate)) && (maxDate == null || !target.isAfter(maxDate))
    val previous = weekStart.minusWeeks(1).takeIf(::allowed)
    val next = weekStart.plusWeeks(1).takeIf(::allowed)
    Column(modifier) {
        Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(teachingWeek?.let { "第 $it 周" } ?: if (weekStart == mondayOf(today)) "本周" else "课表", style = MaterialTheme.typography.titleMedium)
                Text(
                    "${weekStart.monthValue}月${weekStart.dayOfMonth}日 – ${weekStart.plusDays(6).monthValue}月${weekStart.plusDays(6).dayOfMonth}日",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (weekStart != mondayOf(today)) TextButton(onClick = { onWeek(mondayOf(today)) }) { Text("回到本周") }
            IconButton(onClick = { previous?.let(onWeek) }, enabled = previous != null) {
                Icon(Icons.AutoMirrored.Rounded.KeyboardArrowLeft, contentDescription = "上一周")
            }
            IconButton(onClick = { next?.let(onWeek) }, enabled = next != null) {
                Icon(Icons.AutoMirrored.Rounded.KeyboardArrowRight, contentDescription = "下一周")
            }
        }
        if (status != null) {
            EmptyState("暂时无法读取这一周的课表", status)
        } else {
            ScheduleWeekGrid(
                periods = periods,
                days = days,
                today = today,
                display = display,
                colorOf = colorOf,
                zoom = zoom,
                onZoomCommit = onZoomCommit,
                onSwipeWeek = { delta -> (if (delta > 0) next else previous)?.let(onWeek) },
                onBlockClick = onBlockClick,
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SwapScreen(date: String?, index: Int?, onBack: () -> Unit, snackbar: SnackbarHostState, onOpen: (Screen) -> Unit = {}) {
    val bundle by ConnectionManager.schedule.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val classes by ConnectionManager.classes.collectAsState()
    val afterSchool = snapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    // 换课可选插件上报的全部日期（今天起 30 天，旧版插件只有 7 天）。
    val days = availableScheduleDays(bundle, afterSchool)
    var selectedDate by remember { mutableStateOf(date ?: days.firstOrNull()?.date) }
    val day = days.firstOrNull { it.date == selectedDate } ?: days.firstOrNull()
    val courses = day?.courses.orEmpty().filter { it.enabled && day?.enabled == true }
    var sourceIndex by remember { mutableIntStateOf(index ?: courses.firstOrNull()?.index ?: 0) }
    var targetIndex by remember { mutableIntStateOf(courses.firstOrNull { it.index != sourceIndex }?.index ?: sourceIndex) }
    var exchange by remember { mutableStateOf(true) }
    var permanent by remember { mutableStateOf(false) }
    var replacementId by remember { mutableStateOf(bundle?.subjects?.firstOrNull()?.id.orEmpty()) }
    val source = courses.firstOrNull { it.index == sourceIndex }
    val target = courses.firstOrNull { it.index == targetIndex }
    // 与提交命令使用同一套班级权限判断，避免全局权限没有该位时把本班授权的老师挡在入口外。
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val classPermissions = effectiveClassPermissions(classes, currentClassId, user?.permissions ?: 0)
    // 能力快照存在短暂不同步窗口时仍允许提交，由服务端返回 CAPABILITY_UNSUPPORTED 等明确结果。
    val canChange = (classPermissions and Protocol.PERMISSION_MANAGE_SCHEDULE) == Protocol.PERMISSION_MANAGE_SCHEDULE
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(
            title = { Text("快速换课", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
            actions = { IconButton({ onOpen(Screen.Help) }) { Icon(Icons.Rounded.Help, contentDescription = "说明") } },
        )
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Card(
                modifier = Modifier.fillMaxWidth().height(136.dp),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
                shape = androidx.compose.foundation.shape.RoundedCornerShape(28.dp),
            ) {
                Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("当前选择 · ${source?.label ?: "未选择"} ${source?.subject ?: ""}", style = MaterialTheme.typography.titleMedium)
                    Text(
                        listOfNotNull(source?.startTime, source?.endTime).joinToString("–").ifBlank { "请选择一节课" } +
                            "\n从首页进入时会预选当前课或下一节课。",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            if (!canChange) {
                PermissionHint("当前账号没有换课权限。")
            } else if (bundle?.fromProfile == true) {
                PermissionHint("${classDisplayName(classes, currentClassId)}的课表按班级档案推算，需等教室端插件同步实际课表后才能换课。")
            } else if (day == null) {
                EmptyState("没有可换课的日期", "请先同步课表。")
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(3.dp), modifier = Modifier.fillMaxWidth()) {
                    OutlinedButton(
                        onClick = { exchange = true },
                        modifier = Modifier.weight(1f).height(56.dp),
                        shape = connectedButtonShape(0, 2),
                    ) { Text(if (exchange) "交换课程" else "改为交换") }
                    OutlinedButton(
                        onClick = { exchange = false },
                        modifier = Modifier.weight(1f).height(56.dp),
                        shape = connectedButtonShape(1, 2),
                    ) { Text("目标 · ${target?.label ?: "未选"} ${target?.subject ?: ""}") }
                }
                var showTarget by remember { mutableStateOf(false) }
                var showSource by remember { mutableStateOf(false) }
                var showSubject by remember { mutableStateOf(false) }
                var showDate by remember { mutableStateOf(false) }
                OutlinedButton(onClick = { showDate = true }, modifier = Modifier.fillMaxWidth()) {
                    Text("日期：${swapDateLabel(day.date)}")
                }
                val firstDate = days.firstNotNullOfOrNull { parseScheduleDate(it.date) }
                val lastDate = days.lastOrNull()?.let { parseScheduleDate(it.date) }
                if (showDate && firstDate != null && lastDate != null) BoundedDatePickerDialog(
                    initial = parseScheduleDate(day.date),
                    min = firstDate,
                    max = lastDate,
                    onDismiss = { showDate = false },
                    onPick = { picked ->
                        // 换日期后默认选当天第一节，目标为第二节，避免沿用前一天的节次。
                        val pickedCourses = days.firstOrNull { it.date == picked.toString() }
                            ?.takeIf { it.enabled }?.courses.orEmpty().filter { it.enabled }
                        selectedDate = picked.toString()
                        sourceIndex = pickedCourses.firstOrNull()?.index ?: 0
                        targetIndex = pickedCourses.firstOrNull { it.index != sourceIndex }?.index ?: sourceIndex
                    },
                )
                if (courses.isEmpty()) {
                    Text("这一天没有课，请选择其他日期。", style = MaterialTheme.typography.bodyMedium)
                }
                OutlinedButton(onClick = { showSource = true }, modifier = Modifier.fillMaxWidth()) {
                    Text("源课程：${source?.label ?: "未选"} ${source?.subject ?: ""}")
                }
                if (exchange) {
                    OutlinedButton(onClick = { showTarget = true }, modifier = Modifier.fillMaxWidth()) {
                        Text("目标课程：${target?.label ?: "未选"} ${target?.subject ?: ""}")
                    }
                } else {
                    OutlinedButton(onClick = { showSubject = true }, modifier = Modifier.fillMaxWidth()) {
                        Text("替换为：${bundle?.subjects?.firstOrNull { it.id == replacementId }?.name ?: "选择科目"}")
                    }
                }
                TextButton(onClick = { exchange = !exchange }) { Text(if (exchange) "改为替换课程" else "改为交换课程") }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("永久换课", style = MaterialTheme.typography.bodyLarge)
                        Text("写入源课表，之后每周继续生效", style = MaterialTheme.typography.bodySmall)
                    }
                    Switch(checked = permanent, onCheckedChange = { permanent = it })
                }
                DropdownMenu(expanded = showSource, onDismissRequest = { showSource = false }) {
                    courses.forEach {
                        DropdownMenuItem({ Text("${it.label} ${it.subject}") }, {
                            sourceIndex = it.index
                            showSource = false
                        })
                    }
                }
                DropdownMenu(expanded = showTarget, onDismissRequest = { showTarget = false }) {
                    courses.filter { it.index != sourceIndex }.forEach {
                        DropdownMenuItem({ Text("${it.label} ${it.subject}") }, {
                            targetIndex = it.index
                            showTarget = false
                        })
                    }
                }
                DropdownMenu(expanded = showSubject, onDismissRequest = { showSubject = false }) {
                    bundle?.subjects.orEmpty().forEach {
                        DropdownMenuItem({ Text(it.name) }, {
                            replacementId = it.id
                            showSubject = false
                        })
                    }
                }
                Button(
                    onClick = {
                        val currentDay = day
                        if (currentDay == null || source == null) {
                            scope.launch { snackbar.showSnackbar("请选择要调整的课程") }
                            return@Button
                        }
                        if (exchange && (target == null || target.index == source.index)) {
                            scope.launch { snackbar.showSnackbar("请选择不同的目标节次") }
                            return@Button
                        }
                        if (!exchange && replacementId.isBlank()) {
                            scope.launch { snackbar.showSnackbar("请选择替换科目") }
                            return@Button
                        }
                        ConnectionManager.sendScheduleChange(
                            ScheduleChangeRequest(
                                date = currentDay.date,
                                mode = if (exchange) Protocol.CHANGE_EXCHANGE else Protocol.CHANGE_REPLACE,
                                sourceIndex = source.index,
                                targetIndex = if (exchange) target?.index else null,
                                replacementSubjectId = if (exchange) null else replacementId,
                                expectedRevision = currentDay.revision,
                                permanent = permanent,
                            ),
                        )
                    },
                    modifier = Modifier.fillMaxWidth().height(56.dp),
                    enabled = canChange && courses.isNotEmpty(),
                ) { Text("确认换课") }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HelpScreen(onBack: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("换课说明") },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
        )
        Text(
            "交换会互换同一天的两节课；替换会把源课程改成选定科目。日期可选今天起 30 天内的任意一天。提交前会校验课表修订号，避免覆盖别人刚改过的课表。成功后今日课堂、课表和在线手表都会更新。",
            modifier = Modifier.padding(16.dp),
            style = MaterialTheme.typography.bodyLarge,
        )
    }
}

@Composable
fun subjectIcon(subject: String): ImageVector {
    val name = subject
    return when {
        "体育" in name || "跑" in name -> Icons.Rounded.DirectionsRun
        "信息" in name || "计算机" in name || "编程" in name -> Icons.Rounded.Computer
        "数学" in name -> Icons.Rounded.Functions
        else -> Icons.Rounded.CalendarMonth
    }
}

private fun teacherSlotRange(slot: MyCourseSlot): String = listOfNotNull(slot.course.startTime, slot.course.endTime)
    .joinToString("–").ifBlank { slot.startsAt.take(16).replace('T', ' ') }

private fun teacherProgress(startsAt: String, endsAt: String): Float {
    val start = runCatching { OffsetDateTime.parse(startsAt).toInstant().toEpochMilli() }.getOrNull() ?: return 0f
    val end = runCatching { OffsetDateTime.parse(endsAt).toInstant().toEpochMilli() }.getOrNull() ?: return 0f
    if (end <= start) return 0f
    return ((System.currentTimeMillis() - start).toFloat() / (end - start)).coerceIn(0f, 1f)
}

fun schedulePullActionEnabled(state: ConnectionManager.SchedulePullState): Boolean =
    state !is ConnectionManager.SchedulePullState.Pulling
