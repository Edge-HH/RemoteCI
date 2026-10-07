package com.remoteci.mobile.ui

import android.os.SystemClock
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.material.icons.rounded.Menu
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material.icons.rounded.Today
import androidx.compose.material.icons.rounded.Tune
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
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Scaffold
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
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.MyScheduleResponse
import com.remoteci.mobile.data.MyCourseSlot
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.ScheduleChangeRequest
import com.remoteci.mobile.data.ScheduleDay
import com.remoteci.mobile.data.WatchSettings
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
    var server by remember { mutableStateOf(settings.cloudServerUrl) }
    val pendingSetup by ConnectionManager.pendingPasswordSetup.collectAsState()
    var newPassword by remember { mutableStateOf("") }
    var confirmNewPassword by remember { mutableStateOf("") }
    var settingUp by remember { mutableStateOf(false) }
    val plugins by ConnectionManager.lanPlugins.collectAsState()
    val scanStatus by ConnectionManager.lanDiscoveryStatus.collectAsState()
    val scanning by ConnectionManager.lanDiscoveryScanning.collectAsState()
    val pending by ConnectionManager.lanBootstrapPending.collectAsState()
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
    Scaffold { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).padding(16.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("登录 RemoteCI", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
            Text("输入服务器地址并用账号登录，或扫描 WebUI 的登录二维码直接登录。", color = MaterialTheme.colorScheme.onSurfaceVariant)
            OutlinedTextField(server, { server = it }, label = { Text("服务器地址") }, modifier = Modifier.fillMaxWidth(), trailingIcon = {
                IconButton({ scanner.launch(qrScanOptions("扫描登录二维码或服务器地址")) }) {
                    Icon(Icons.Rounded.QrCodeScanner, contentDescription = "扫描")
                }
            })
            OutlinedTextField(username, { username = it }, label = { Text("ID") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(
                password,
                { password = it },
                label = { Text("密码") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            )
            if (server.trim().startsWith("http://")) {
                Text("当前是明文 HTTP，请确认网络可信。", color = MaterialTheme.colorScheme.error)
            }
            if (pendingSetup != null) {
                // 首登设置密码：批量导入的账号首次登录时在这里补设密码。
                Card {
                    Column(
                        Modifier.fillMaxWidth().padding(16.dp),
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
                        )
                        OutlinedTextField(
                            confirmNewPassword,
                            { confirmNewPassword = it },
                            label = { Text("确认新密码") },
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true,
                            visualTransformation = PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                        )
                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            Button(
                                onClick = {
                                    if (newPassword.length < 8) {
                                        scope.launch { snackbar.showSnackbar("密码需为 8-128 个字符") }
                                    } else if (newPassword != confirmNewPassword) {
                                        scope.launch { snackbar.showSnackbar("两次输入的新密码不一致") }
                                    } else {
                                        settingUp = true
                                        scope.launch {
                                            val ok = runCatching {
                                                ConnectionManager.setupInitialPassword(newPassword)
                                            }.getOrElse { false }
                                            settingUp = false
                                            if (ok) {
                                                password = newPassword
                                                newPassword = ""
                                                confirmNewPassword = ""
                                                ConnectionManager.connect(settings.copy(username = pendingSetup!!.username), password)
                                                snackbar.showSnackbar("密码已设置，正在登录")
                                            } else {
                                                snackbar.showSnackbar("设置失败，请重试或联系管理员")
                                            }
                                        }
                                    }
                                },
                                enabled = !settingUp,
                            ) { Text(if (settingUp) "正在保存…" else "保存密码并登录") }
                        }
                    }
                }
            }
            Button(
                onClick = {
                    val next = settings.copy(username = username.trim(), cloudServerUrl = server.trim())
                    onSettings(next)
                    if (username.isBlank() || password.isBlank() || server.isBlank()) {
                        scope.launch { snackbar.showSnackbar("请填写服务器、ID 和密码") }
                    } else ConnectionManager.connect(next, password)
                },
                modifier = Modifier.fillMaxWidth().height(56.dp),
                enabled = connection !is ConnectionManager.State.Connecting,
            ) { Text(if (connection is ConnectionManager.State.Connecting) "正在连接…" else "登录") }
            OutlinedButton(onClick = { ConnectionManager.scanLanPlugins() }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = !scanning) {
                Text(if (scanning) "正在扫描…" else "扫描局域网插件")
            }
            scanStatus?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            plugins.forEach { plugin ->
                FilledTonalButton(onClick = {
                    scope.launch {
                        val updated = ConnectionManager.loadLanBootstrap(settings.copy(username = username.trim()), plugin)
                        if (updated != null) {
                            onSettings(updated)
                            server = updated.cloudServerUrl
                        }
                    }
                }, modifier = Modifier.fillMaxWidth()) { Text("${plugin.instanceName} · ${plugin.host}") }
            }
            pending?.let { (candidate, updated) ->
                Button(onClick = {
                    ConnectionManager.confirmLanBootstrap()?.let {
                        onSettings(it)
                        server = it.cloudServerUrl
                    }
                }, modifier = Modifier.fillMaxWidth()) { Text("确认使用 ${candidate.instanceName} 的云服务器") }
            }
            if (connection is ConnectionManager.State.Error) {
                Text(connection.message, color = MaterialTheme.colorScheme.error)
            }
        }
    }
}

/** 登录后的班级选择页：多班级账号先选一个进入，进入后可在左上角“账号与设置”中切换。 */
@Composable
fun ClassPickerScreen(onPicked: () -> Unit) {
    val classes by ConnectionManager.classes.collectAsState()
    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            modifier = Modifier.fillMaxSize().padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
        ) {
            Text("选择进入的班级", style = MaterialTheme.typography.headlineSmall)
            Text(
                "该账号拥有多个班级的访问权限，进入后可在左上角菜单中切换。",
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
    tab: HomeTab,
    settings: WatchSettings,
    snackbar: SnackbarHostState,
    onTab: (HomeTab) -> Unit,
    onOpen: (Screen) -> Unit,
    onPersist: (WatchSettings) -> Unit,
) {
    val user by ConnectionManager.currentUser.collectAsState()
    val pendingSwaps by ConnectionManager.pendingSwapCount.collectAsState()
    val tabs = visibleHomeTabs(
        canRequestSwap = user?.has(com.remoteci.mobile.data.Protocol.PERMISSION_REQUEST_SWAP) == true,
        canManage = visibleManagementItems(user).isNotEmpty(),
    )
    // 失去换课或管理权限后停留在对应页时退回“今天”。
    LaunchedEffect(tab, tabs, user) { if (user != null && tab !in tabs) onTab(HomeTab.Today) }
    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surfaceContainer) {
                data class Item(val tab: HomeTab, val label: String, val icon: ImageVector)
                listOf(
                    Item(HomeTab.Today, "今天", Icons.Rounded.Today),
                    Item(HomeTab.Schedule, "课表", Icons.Rounded.CalendarMonth),
                    Item(HomeTab.SwapRequests, "换课", Icons.Rounded.SwapHoriz),
                    Item(HomeTab.Control, "控制", Icons.Rounded.Tune),
                    Item(HomeTab.People, "管理", Icons.Rounded.AdminPanelSettings),
                ).filter { it.tab in tabs }.forEach { item ->
                    NavigationBarItem(
                        selected = tab == item.tab,
                        onClick = { onTab(item.tab) },
                        icon = {
                            if (item.tab == HomeTab.SwapRequests && pendingSwaps > 0) {
                                BadgedBox(badge = { Badge { Text(pendingSwaps.toString()) } }) {
                                    Icon(item.icon, contentDescription = item.label)
                                }
                            } else {
                                Icon(item.icon, contentDescription = item.label)
                            }
                        },
                        label = { Text(item.label, style = MaterialTheme.typography.labelMedium) },
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (tab) {
                HomeTab.Today -> TodayScreen(snackbar, onOpen, onTab)
                HomeTab.Schedule -> ScheduleScreen(embedded = true, snackbar = snackbar, onOpen = onOpen, onBack = { onTab(HomeTab.Today) })
                HomeTab.SwapRequests -> SwapRequestsScreen(snackbar = snackbar)
                HomeTab.Control -> ControlListScreen(embedded = true, snackbar = snackbar, onOpen = onOpen, onBack = { onTab(HomeTab.Today) })
                HomeTab.People -> PeopleScreen(embedded = true, snackbar = snackbar, onOpen = onOpen, onBack = { onTab(HomeTab.Today) })
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TodayScreen(snackbar: SnackbarHostState, onOpen: (Screen) -> Unit, onTab: (HomeTab) -> Unit) {
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val schedule by ConnectionManager.schedule.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val personalNext by ConnectionManager.personalNextCourse.collectAsState()
    val connection by ConnectionManager.state.collectAsState()
    val status by ConnectionManager.schedulePullState.collectAsState()
    val admin = remember { mutableStateOf<com.remoteci.mobile.data.AdminStatus?>(null) }
    var nowTick by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    var generatedElapsed by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(snapshot?.generatedAt) { generatedElapsed = SystemClock.elapsedRealtime() }
    LaunchedEffect(Unit) { while (true) { delay(1000); nowTick = SystemClock.elapsedRealtime() } }
    LaunchedEffect(connection) {
        admin.value = runCatching { com.remoteci.mobile.data.AdminApi.adminStatus() }.getOrNull()
    }
    val today = pluginToday(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, generatedElapsed, nowTick)
    val now = pluginLocalNow(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, generatedElapsed, nowTick)
    val day = schedule?.days?.firstOrNull { it.date == today.toString() }
    val home = homeCourseContent(snapshot)
    val teacherMode = user?.isTeacher == true
    val teacherCurrent = personalNext?.current
    val teacherNext = personalNext?.next
    val progress = if (teacherMode) teacherCurrent?.let { teacherProgress(it.startsAt, it.endsAt) } ?: 0f
        else if (shouldShowStateProgress(snapshot)) lessonProgress(snapshot?.currentTimeLayoutItem, now) else 0f
    val period = if (teacherMode) teacherCurrent?.course?.label ?: teacherNext?.course?.label ?: "—"
        else extractPeriod(home.timeLayoutItem) ?: snapshot?.currentTimeLayoutItem?.substringBefore(' ') ?: "—"
    val room = if (teacherMode) listOfNotNull(teacherCurrent?.className, teacherNext?.className).firstOrNull().orEmpty()
        else home.timeLayoutItem?.substringAfter(' ', missingDelimiterValue = "")?.ifBlank { snapshot?.classPlanName }.orEmpty()
    val timeRange = if (teacherMode) teacherCurrent?.let(::teacherSlotRange) ?: teacherNext?.let(::teacherSlotRange) ?: "—"
        else extractTimeRange(home.timeLayoutItem).ifBlank { "—" }
    val stateTitle = if (teacherMode) {
        when {
            teacherCurrent != null -> "上课"
            teacherNext != null -> "即将上课"
            else -> "放学"
        }
    } else when (snapshot?.currentState) {
        Protocol.STATE_CLASS -> "上课"
        Protocol.STATE_BREAKING -> "课间"
        Protocol.STATE_PREPARE_CLASS -> "即将上课"
        Protocol.STATE_AFTER_SCHOOL -> "放学"
        else -> "待机"
    }
    val connected = connection is ConnectionManager.State.CloudConnected || connection is ConnectionManager.State.LanConnected
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopAppBar(
            title = { Text("RemoteCI", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = { IconButton({ onOpen(Screen.Account) }) { Icon(Icons.Rounded.Menu, contentDescription = "菜单") } },
            actions = { IconButton({ onOpen(Screen.Inbox) }) { Icon(Icons.Rounded.Notifications, contentDescription = "通知") } },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.surface),
        )
        Card(
            onClick = {
                if (teacherMode) onTab(HomeTab.Schedule)
                else {
                    val index = homeQuickSwapLessonIndex(day, snapshot)
                    onOpen(Screen.Swap(day?.date, index))
                }
            },
            modifier = Modifier.fillMaxWidth().height(168.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
            shape = androidx.compose.foundation.shape.RoundedCornerShape(20.dp),
        ) {
            Box(Modifier.fillMaxSize().padding(20.dp)) {
                Column {
                    Text(stateTitle, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
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
        val next = if (teacherMode) teacherNext?.course?.subject else nextClassSubjectAfterCurrent(snapshot)
        ConnectedListCard {
            if ((teacherMode || shouldShowNextLessonSummary(snapshot?.currentState)) && !next.isNullOrBlank()) {
                AppListItem(
                    title = "下一节 · $next",
                    supporting = if (teacherMode) listOfNotNull(teacherNext?.className, teacherNext?.let(::teacherSlotRange)).joinToString(" · ")
                    else listOfNotNull(extractPeriod(snapshot?.nextClassTimeLayoutItem), extractTimeRange(snapshot?.nextClassTimeLayoutItem)).joinToString(" ") +
                        (snapshot?.nextClassTimeLayoutItem?.substringAfter(' ', "")?.let { " · $it" } ?: ""),
                    leading = Icons.Rounded.DirectionsRun,
                    trailing = Icons.Rounded.SwapHoriz,
                    index = 0,
                    count = 2,
                    onClick = {
                        if (teacherMode) onTab(HomeTab.Schedule)
                        else onOpen(Screen.Swap(day?.date, nextQuickSwapLessonIndex(day, snapshot)))
                    },
                )
            } else {
                AppListItem("下一节", "目前没有下一节课程", Icons.Rounded.DirectionsRun, index = 0, count = 2)
            }
            val syncText = when (val currentStatus = status) {
                is ConnectionManager.SchedulePullState.Pulling -> currentStatus.message
                is ConnectionManager.SchedulePullState.Success -> currentStatus.message
                is ConnectionManager.SchedulePullState.Error -> currentStatus.message
                else -> formatLocalSyncTime(snapshot?.generatedAt)?.let { "课表已于 $it 同步" } ?: "尚未同步课表"
            }
            val watchText = if ((admin.value?.watchConnections ?: 0) > 0) "手表已连接" else "手表未连接"
            AppListItem(
                title = if (connected) "RemoteCI 已连接" else "RemoteCI 未连接",
                supporting = "$syncText · $watchText",
                leading = if (connected) Icons.Rounded.CloudDone else Icons.Rounded.CloudOff,
                trailing = if (connected) Icons.Rounded.CheckCircle else null,
                index = 1,
                count = 2,
            )
        }
        ConnectedButtons(
            firstLabel = "课表",
            secondLabel = "控制",
            onFirst = { onTab(HomeTab.Schedule) },
            onSecond = { onTab(HomeTab.Control) },
        )
        Spacer(Modifier.height(16.dp))
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScheduleScreen(
    embedded: Boolean,
    snackbar: SnackbarHostState,
    onOpen: (Screen) -> Unit,
    onBack: (() -> Unit)?,
) {
    val bundle by ConnectionManager.schedule.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val pull by ConnectionManager.schedulePullState.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val classes by ConnectionManager.classes.collectAsState()
    val afterSchool = snapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    val days = availableScheduleDays(bundle, afterSchool)
    var selected by remember { mutableIntStateOf(0) }
    if (selected >= days.size) selected = 0
    // 权限按当前班级计算；老师/自定义角色可能只在某个班级拥有换课权限。
    val currentClassId by ConnectionManager.currentClassId.collectAsState()
    val classPermissions = effectiveClassPermissions(classes, currentClassId, user?.permissions ?: 0)
    // 换课是否可执行由服务端按班级权限和插件能力最终复核。
    // 能力快照可能在连接切换/插件刚上线时短暂缺失，不能因此把 Android 入口直接禁用。
    val canChange = (classPermissions and Protocol.PERMISSION_MANAGE_SCHEDULE) == Protocol.PERMISSION_MANAGE_SCHEDULE
    val canPull = user?.canPullScheduleFor(currentClassId) == true

    // 老师与班主任：显示名与课表教师名绑定后，可跨班级查看“我的日程”；null 表示尚未手动切换。
    // 老师默认进入“我的日程”，班主任默认仍看本班课表。
    var mineChoice by remember { mutableStateOf<Boolean?>(null) }
    // 委派属性无法智能转换，先取局部值再判断，避免 user 为 null 时误读。
    val mineMode = user?.let { it.hasPersonalSchedule && (mineChoice ?: it.isTeacher) } == true
    var mySchedule by remember { mutableStateOf<MyScheduleResponse?>(null) }
    var myScheduleStatus by remember { mutableStateOf<String?>(null) }
    var myScheduleRefresh by remember { mutableIntStateOf(0) }
    var mySelected by remember { mutableIntStateOf(0) }
    LaunchedEffect(user?.id, mineMode, myScheduleRefresh) {
        if (mineMode && user != null) {
            runCatching { AdminApi.mySchedule() }
                .onSuccess { mySchedule = it; myScheduleStatus = null }
                .onFailure { myScheduleStatus = it.message ?: "暂时无法读取我的日程" }
        }
    }
    val myDays = mySchedule?.days.orEmpty()
    if (mySelected >= myDays.size) mySelected = 0

    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("课表", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = {
                IconButton(onClick = { onBack?.invoke() ?: onOpen(Screen.Home(HomeTab.Today)) }) {
                    Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回")
                }
            },
            actions = {
                IconButton(
                    onClick = { if (mineMode) myScheduleRefresh++ else ConnectionManager.requestSchedulePull() },
                    enabled = mineMode || (canPull && schedulePullActionEnabled(pull)),
                ) {
                    Icon(Icons.Rounded.Refresh, contentDescription = "刷新")
                }
            },
        )
        if (user?.hasPersonalSchedule == true) {
            Row(
                Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                FilterChip(selected = mineMode, onClick = { mineChoice = true }, label = { Text("我的日程") })
                FilterChip(selected = !mineMode, onClick = { mineChoice = false }, label = { Text("本班课表") })
            }
        }
        if (mineMode) {
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
                            Text(item.className, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
                            ConnectedListCard {
                                item.courses.forEachIndexed { index, course ->
                                    val time = listOfNotNull(course.startTime, course.endTime).joinToString("–")
                                    AppListItem(
                                        title = "${course.label} · ${course.subject}",
                                        supporting = listOfNotNull(time.ifEmpty { null }, course.teacher).joinToString(" · "),
                                        leading = subjectIcon(course.subject),
                                        index = index,
                                        count = item.courses.size,
                                    )
                                }
                            }
                        }
                    }
                }
            }
        } else {
        if (days.isEmpty()) {
            EmptyState("还没有课表", if (canPull) "连接成功后会显示未来七日课程。管理员或班主任可刷新课表。" else "连接成功后会显示未来七日课程。")
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
                if (courses.isEmpty()) {
                    EmptyState("这一天没有课程", if (canPull) "管理员或班主任可拉取最新课表，或选择其他日期。" else "请选择其他日期。")
                } else {
                    ConnectedListCard {
                        courses.forEachIndexed { index, course ->
                            val time = listOfNotNull(course.startTime, course.endTime).joinToString("–")
                            AppListItem(
                                title = "${course.label} · ${course.subject}",
                                supporting = listOfNotNull(time.ifEmpty { null }, course.teacher).joinToString(" · "),
                                leading = subjectIcon(course.subject),
                                trailing = if (canChange) Icons.Rounded.Edit else null,
                                index = index,
                                count = courses.size,
                                onClick = if (canChange) ({ onOpen(Screen.Swap(day.date, course.index)) }) else null,
                            )
                        }
                    }
                }
                if (canPull) {
                    ConnectedButtons(
                        firstLabel = "拉取课表",
                        secondLabel = "换课",
                        onFirst = { ConnectionManager.requestSchedulePull() },
                        onSecond = { onOpen(Screen.Swap(day.date, courses.firstOrNull()?.index)) },
                        firstFilled = false,
                    )
                } else if (canChange) {
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

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SwapScreen(date: String?, index: Int?, onBack: () -> Unit, snackbar: SnackbarHostState, onOpen: (Screen) -> Unit = {}) {
    val bundle by ConnectionManager.schedule.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val user by ConnectionManager.currentUser.collectAsState()
    val classes by ConnectionManager.classes.collectAsState()
    val afterSchool = snapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    val days = availableScheduleDays(bundle, afterSchool)
    var selectedDate by remember { mutableStateOf(date ?: days.firstOrNull()?.date) }
    val day = days.firstOrNull { it.date == selectedDate } ?: days.firstOrNull()
    val courses = day?.courses.orEmpty().filter { it.enabled }
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
                shape = androidx.compose.foundation.shape.RoundedCornerShape(20.dp),
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
                    enabled = canChange,
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
            "交换会互换两节课；替换会把源课程改成选定科目。提交前会校验课表修订号，避免覆盖别人刚改过的课表。成功后今日课堂、七日课表和在线手表都会更新。",
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
