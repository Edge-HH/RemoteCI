package com.remoteci.watch.ui

import android.content.Context
import android.os.SystemClock
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.res.stringResource
import com.remoteci.watch.R
import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.EventHistory
import com.remoteci.watch.data.ExtensionDefinition
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleChangeRequest
import com.remoteci.watch.data.SettingsStore
import com.remoteci.watch.data.SnapshotStore
import com.remoteci.watch.notif.NotificationHelper
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private enum class Screen {
    Login,
    Home,
    ScheduleOverview,
    ScheduleDatePicker,
    DayPicker,
    Swap,
    LessonPicker,
    SubjectPicker,
    Control,
    Notification,
    VoiceMessage,
    ExtensionForm,
    Power,
    Volume,
    Settings,
    ConnectionSettings,
    NotificationSettings,
    AppearanceSettings,
    DeveloperSettings,
    Update,
}

@Composable
fun RemoteCiApp(context: Context) {
    val settingsStore = remember { SettingsStore(context) }
    val snapshotStore = remember { SnapshotStore(context) }
    val eventHistory = remember { EventHistory(context) }
    val currentVersion = remember {
        runCatching {
            context.packageManager.getPackageInfo(context.packageName, 0).versionName
        }.getOrNull() ?: "0.0.0"
    }
    ConnectionManager.initialize(context)
    val scope = rememberCoroutineScope()

    var settings by remember { mutableStateOf(settingsStore.load()) }
    var screen by rememberSaveable {
        mutableStateOf(if (ConnectionManager.hasSavedSession()) Screen.Home else Screen.Login)
    }
    // 密码只保存在内存中：绝不进入 saved instance state（rememberSaveable 会把明文写盘）。
    var password by remember { mutableStateOf("") }
    var selectedDate by rememberSaveable { mutableStateOf<String?>(null) }
    var swapMode by rememberSaveable { mutableStateOf(SwapMode.Exchange) }
    var pickerTarget by rememberSaveable { mutableStateOf(LessonTarget.Source) }
    var sourceIndex by rememberSaveable { mutableStateOf<Int?>(null) }
    var targetIndex by rememberSaveable { mutableStateOf<Int?>(null) }
    var replacementSubjectId by rememberSaveable { mutableStateOf<String?>(null) }
    // 主界面课程按钮直达换课页时标记来源，返回键直接回主页而不是先回日期选择。
    var quickSwapFromHome by rememberSaveable { mutableStateOf(false) }
    var noticeTitle by rememberSaveable { mutableStateOf("") }
    var noticeMessage by rememberSaveable { mutableStateOf("") }
    var noticeEffectEnabled by rememberSaveable { mutableStateOf(false) }
    var noticeSoundEnabled by rememberSaveable { mutableStateOf(false) }
    var noticeSpeechEnabled by rememberSaveable { mutableStateOf(false) }
    var activeExtension by remember { mutableStateOf<ExtensionDefinition?>(null) }
    var cachedSnapshot by remember { mutableStateOf(snapshotStore.load()) }
    var cachedSchedule by remember { mutableStateOf(snapshotStore.loadSchedule()) }

    val connectionState by ConnectionManager.state.collectAsState()
    val connectedServerVersion by ConnectionManager.serverVersion.collectAsState()
    val availableCapabilities by ConnectionManager.availableCapabilities.collectAsState()
    val currentUser by ConnectionManager.currentUser.collectAsState()
    val liveSnapshot by ConnectionManager.snapshot.collectAsState()
    val liveSchedule by ConnectionManager.schedule.collectAsState()
    val liveExtensions by ConnectionManager.extensions.collectAsState()
    val serverSettings by ConnectionManager.settings.collectAsState()
    val commandResult by ConnectionManager.lastCommandResult.collectAsState()
    val schedulePullState by ConnectionManager.schedulePullState.collectAsState()
    val lanPlugins by ConnectionManager.lanPlugins.collectAsState()
    val lanDiscoveryStatus by ConnectionManager.lanDiscoveryStatus.collectAsState()
    val lanDiscoveryScanning by ConnectionManager.lanDiscoveryScanning.collectAsState()
    val lanBootstrapPending by ConnectionManager.lanBootstrapPending.collectAsState()
    val displayedSnapshot = liveSnapshot ?: cachedSnapshot
    val displayedSchedule = liveSchedule ?: cachedSchedule
    val currentSettings by rememberUpdatedState(settings)
    val afterSchool = displayedSnapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    // 日期判断以插件时区推算“今天”，避免手表与插件时区不一致时选错课表日。
    val snapshotElapsedBase = remember(displayedSnapshot?.generatedAt) { SystemClock.elapsedRealtime() }
    val today = pluginToday(
        displayedSnapshot?.generatedAt,
        displayedSnapshot?.timeZoneOffsetMinutes,
        snapshotElapsedBase,
        SystemClock.elapsedRealtime(),
    )
    val selectedDay = displayedSchedule?.days?.firstOrNull { it.date == selectedDate }
    val lessons = remember(selectedDay) { buildLessonChoices(selectedDay) }

    LaunchedEffect(Unit) {
        if (ConnectionManager.hasSavedSession()) ConnectionManager.connect(settings)
        ConnectionManager.events.collect { event ->
            NotificationHelper.handle(context, event, currentSettings, eventHistory)
        }
    }
    LaunchedEffect(Unit) {
        ConnectionManager.discoveredSettings.collect { updated ->
            settings = updated
            withContext(Dispatchers.IO) { settingsStore.save(updated) }
        }
    }
    // 快照推送每秒到达：倒计时字段每秒变化，仅状态字段变化时才落盘，避免持续写 SharedPreferences。
    var persistedSnapshotKey by remember { mutableStateOf(snapshotPersistKey(cachedSnapshot)) }
    LaunchedEffect(liveSnapshot) {
        val snapshot = liveSnapshot ?: return@LaunchedEffect
        cachedSnapshot = snapshot
        val key = snapshotPersistKey(snapshot)
        if (key != persistedSnapshotKey) {
            persistedSnapshotKey = key
            withContext(Dispatchers.IO) { snapshotStore.save(snapshot) }
        }
    }
    LaunchedEffect(liveSchedule) {
        liveSchedule?.let { withContext(Dispatchers.IO) { snapshotStore.saveSchedule(it) }; cachedSchedule = it }
    }
    LaunchedEffect(currentUser, availableCapabilities) {
        val user = currentUser
        if (user != null && screen == Screen.Login) screen = Screen.Home
        if (user != null && (!user.has(Protocol.PERMISSION_MANAGE_SCHEDULE) ||
                Protocol.CAP_SCHEDULE_CHANGE !in availableCapabilities) &&
            screen in listOf(
                Screen.DayPicker,
                Screen.Swap,
                Screen.LessonPicker,
                Screen.SubjectPicker,
            )
        ) screen = Screen.Home
        if (user != null && (!user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) ||
                Protocol.CAP_NOTIFICATION_SEND !in availableCapabilities) && screen == Screen.Notification)
            screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_POWER_CONTROL) &&
            screen in listOf(Screen.Power, Screen.Volume))
            screen = Screen.Home
        if ((user == null || !user.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) ||
                Protocol.CAP_VOICE_MESSAGE_SEND !in availableCapabilities) && screen == Screen.VoiceMessage)
            screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) &&
            !user.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) &&
            !user.has(Protocol.PERMISSION_POWER_CONTROL) &&
            !user.has(Protocol.PERMISSION_MAIN_MENU_CONTROL) &&
            !user.has(Protocol.PERMISSION_TEACHER_COMING) &&
            !user.has(Protocol.PERMISSION_RUN_EXTENSIONS) && screen == Screen.Control)
            screen = Screen.Home
        val active = activeExtension
        if (user != null && active != null &&
            !user.canInvoke(active) && screen == Screen.ExtensionForm)
            screen = Screen.Control
    }
    LaunchedEffect(selectedDay) {
        val enabled = lessons.filter { it.enabled }
        if (enabled.none { it.index == sourceIndex }) sourceIndex = enabled.firstOrNull()?.index
        if (enabled.none { it.index == targetIndex } || targetIndex == sourceIndex)
            targetIndex = enabled.firstOrNull { it.index != sourceIndex }?.index
        if (displayedSchedule?.subjects?.none { it.id == replacementSubjectId } != false)
            replacementSubjectId = displayedSchedule?.subjects?.firstOrNull()?.id
    }

    BackHandler(enabled = screen !in listOf(Screen.Home, Screen.Login)) {
        screen = when (screen) {
            Screen.LessonPicker, Screen.SubjectPicker -> Screen.Swap
            Screen.Swap -> if (quickSwapFromHome) {
                quickSwapFromHome = false
                Screen.Home
            } else {
                Screen.DayPicker
            }
            Screen.ScheduleDatePicker -> Screen.ScheduleOverview
            Screen.Notification -> Screen.Control
            Screen.VoiceMessage -> Screen.Control
            Screen.ExtensionForm -> Screen.Control
            Screen.Power -> Screen.Control
            Screen.Volume -> Screen.Control
            Screen.ConnectionSettings, Screen.NotificationSettings, Screen.DeveloperSettings -> Screen.Settings
            Screen.AppearanceSettings -> Screen.Settings
            Screen.Update -> Screen.Settings
            else -> Screen.Home
        }
    }

    AppTheme(palette = WatchPalette.fromId(settings.themeId)) {
        when (screen) {
            Screen.Login -> LoginScreen(
                settings = settings,
                password = password,
                state = connectionState,
                onSettingsChange = { settings = it },
                onPasswordChange = { password = it },
                lanPlugins = lanPlugins,
                lanDiscoveryStatus = lanDiscoveryStatus,
                lanDiscoveryScanning = lanDiscoveryScanning,
                lanBootstrapPending = lanBootstrapPending?.first,
                onScanLanPlugins = ConnectionManager::scanLanPlugins,
                onSelectLanPlugin = { candidate ->
                    scope.launch {
                        val updated = ConnectionManager.loadLanBootstrap(settings, candidate) ?: return@launch
                        settings = updated
                        settingsStore.save(updated)
                    }
                },
                onConfirmLanBootstrap = {
                    val confirmed = ConnectionManager.confirmLanBootstrap() ?: return@LoginScreen
                    settings = confirmed
                    settingsStore.save(confirmed)
                },
                onLogin = {
                    settingsStore.save(settings)
                    ConnectionManager.connect(settings, password)
                    password = ""
                },
            )

            Screen.Home -> {
                val scheduleDate = displayedSnapshot?.scheduleDate ?: today.toString()
                val scheduleDay = displayedSchedule?.days?.firstOrNull { it.date == scheduleDate }
                val canSwap = currentUser?.has(Protocol.PERMISSION_MANAGE_SCHEDULE) == true &&
                    Protocol.CAP_SCHEDULE_CHANGE in availableCapabilities
                val displayedCourseIndex = if (canSwap) {
                    homeQuickSwapLessonIndex(scheduleDay, displayedSnapshot)
                } else null
                val nextCourseIndex = if (canSwap) {
                    nextQuickSwapLessonIndex(scheduleDay, displayedSnapshot)
                } else null

                // 两个首页入口共用同一跳转流程，但分别传入当前显示课程和下一节课的索引。
                fun openQuickSwap(index: Int) {
                    selectedDate = scheduleDate
                    sourceIndex = index
                    quickSwapFromHome = true
                    screen = Screen.Swap
                }

                HomeScreen(
                    connectionState = connectionState,
                    snapshot = displayedSnapshot,
                    user = currentUser,
                    capabilities = availableCapabilities,
                    onOpenScheduleOverview = {
                        selectedDate = initialScheduleDate(displayedSchedule, afterSchool, today)
                        screen = Screen.ScheduleOverview
                    },
                    onOpenScheduleChange = {
                        selectedDate = initialScheduleDate(displayedSchedule, afterSchool, today)
                        screen = Screen.DayPicker
                    },
                    onQuickSwapCourse = displayedCourseIndex?.let { index -> { openQuickSwap(index) } },
                    onQuickSwapNextCourse = nextCourseIndex?.let { index -> { openQuickSwap(index) } },
                    onOpenNotification = { screen = Screen.Control },
                    onOpenSettings = { screen = Screen.Settings },
                    onRetryConnection = {
                        if (ConnectionManager.hasSavedSession()) ConnectionManager.connect(settings) else screen = Screen.Login
                    },
                )
            }

            Screen.ScheduleOverview -> ScheduleOverviewScreen(
                day = selectedDay,
                today = today,
                connectionReady = (connectionState is ConnectionManager.State.LanConnected ||
                    connectionState is ConnectionManager.State.CloudConnected) &&
                    Protocol.CAP_SCHEDULE_PULL in availableCapabilities,
                pullState = schedulePullState,
                onRequestSchedule = ConnectionManager::requestSchedulePull,
                onPickDate = { screen = Screen.ScheduleDatePicker },
                onBack = { screen = Screen.Home },
            )

            Screen.ScheduleDatePicker -> ScheduleDatePickerScreen(
                bundle = displayedSchedule,
                afterSchool = afterSchool,
                today = today,
                onSelect = { day -> selectedDate = day.date; screen = Screen.ScheduleOverview },
                onBack = { screen = Screen.ScheduleOverview },
            )

            Screen.DayPicker -> DayPickerScreen(
                bundle = displayedSchedule,
                afterSchool = afterSchool,
                today = today,
                onSelect = { day -> selectedDate = day.date; screen = Screen.Swap },
                onBack = { screen = Screen.Home },
            )

            Screen.Swap -> SwapScreen(
                day = selectedDay,
                mode = swapMode,
                sourceLesson = lessons.firstOrNull { it.index == sourceIndex },
                targetLesson = lessons.firstOrNull { it.index == targetIndex },
                replacementSubject = displayedSchedule?.subjects?.firstOrNull { it.id == replacementSubjectId }?.name,
                connectionReady = (connectionState is ConnectionManager.State.LanConnected ||
                    connectionState is ConnectionManager.State.CloudConnected) &&
                    Protocol.CAP_SCHEDULE_CHANGE in availableCapabilities,
                resultText = commandResult?.let {
                    context.getString(if (it.success) R.string.result_success else R.string.result_failure, it.message)
                },
                onModeChange = { swapMode = it },
                onPickSource = { pickerTarget = LessonTarget.Source; screen = Screen.LessonPicker },
                onPickTarget = {
                    screen = if (swapMode == SwapMode.Exchange) {
                        pickerTarget = LessonTarget.Target
                        Screen.LessonPicker
                    } else Screen.SubjectPicker
                },
                onSubmit = {
                    val day = selectedDay ?: return@SwapScreen
                    val source = sourceIndex ?: return@SwapScreen
                    ConnectionManager.sendScheduleChange(
                        ScheduleChangeRequest(
                            date = day.date,
                            mode = if (swapMode == SwapMode.Exchange) Protocol.CHANGE_EXCHANGE else Protocol.CHANGE_REPLACE,
                            sourceIndex = source,
                            targetIndex = if (swapMode == SwapMode.Exchange) targetIndex else null,
                            replacementSubjectId = if (swapMode == SwapMode.Replace) replacementSubjectId else null,
                            expectedRevision = day.revision,
                        ),
                    )
                },
            )

            Screen.LessonPicker -> LessonPickerScreen(
                title = stringResource(if (pickerTarget == LessonTarget.Source) R.string.pick_source_lesson else R.string.pick_target_lesson),
                lessons = lessons,
                selectedIndex = if (pickerTarget == LessonTarget.Source) sourceIndex else targetIndex,
                excludedIndex = if (pickerTarget == LessonTarget.Source) targetIndex else sourceIndex,
                onSelect = {
                    if (pickerTarget == LessonTarget.Source) sourceIndex = it.index else targetIndex = it.index
                    screen = Screen.Swap
                },
            )

            Screen.SubjectPicker -> SubjectPickerScreen(
                subjects = displayedSchedule?.subjects.orEmpty(),
                selectedSubjectId = replacementSubjectId,
                onSelect = { replacementSubjectId = it.id; screen = Screen.Swap },
            )

            Screen.Control -> ControlScreen(
                snapshot = displayedSnapshot,
                user = currentUser,
                capabilities = availableCapabilities,
                extensions = liveExtensions,
                resultText = commandResult?.let {
                    context.getString(if (it.success) R.string.result_success else R.string.result_failure, it.message)
                },
                onTeacherComing = ConnectionManager::teacherComing,
                onOpenNotification = { screen = Screen.Notification },
                onOpenVoiceMessage = { screen = Screen.VoiceMessage },
                onClearNotifications = ConnectionManager::clearNotifications,
                onToggleMainMenu = {
                    ConnectionManager.setMainMenuVisible(!(displayedSnapshot?.isMainMenuVisible ?: true))
                },
                onOpenVolume = { screen = Screen.Volume },
                onOpenPower = { screen = Screen.Power },
                onRunExtension = { extension ->
                    if (extension.parameters.isEmpty()) {
                        ConnectionManager.runExtension(extension)
                    } else {
                        activeExtension = extension
                        screen = Screen.ExtensionForm
                    }
                },
                onBack = { screen = Screen.Home },
            )

            Screen.ExtensionForm -> {
                val extension = activeExtension
                if (extension == null) {
                    // 进程重建后内存中的扩展清单尚未重新同步，退回控制菜单等待重新推送。
                    LaunchedEffect(Unit) { screen = Screen.Control }
                } else {
                    ExtensionFormScreen(
                        extension = extension,
                        connectionReady = connectionState is ConnectionManager.State.LanConnected ||
                            connectionState is ConnectionManager.State.CloudConnected,
                        resultText = commandResult?.message,
                        onSubmit = { args ->
                            ConnectionManager.runExtension(extension, args)
                            screen = Screen.Control
                        },
                        onBack = { screen = Screen.Control },
                    )
                }
        }

        Screen.Power -> PowerScreen(
            sleepAvailable = displayedSnapshot?.isSleepAvailable == true,
            hibernateAvailable = displayedSnapshot?.isHibernateAvailable == true,
            onPowerAction = ConnectionManager::sendPowerAction,
            onBack = { screen = Screen.Control },
        )

        Screen.Volume -> VolumeScreen(
            volumePercent = displayedSnapshot?.volumePercent ?: 0,
            muted = displayedSnapshot?.isMuted == true,
            available = displayedSnapshot?.isVolumeControlAvailable == true,
            onVolumeChange = ConnectionManager::setVolume,
            onMutedChange = ConnectionManager::setMuted,
            onBack = { screen = Screen.Control },
        )

        Screen.VoiceMessage -> VoiceMessageScreen(
            connectionReady = connectionState is ConnectionManager.State.LanConnected ||
                connectionState is ConnectionManager.State.CloudConnected,
            onBack = { screen = Screen.Control },
        )

        Screen.Notification -> NotificationScreen(
            title = noticeTitle,
            message = noticeMessage,
            forceSenderInTitle = serverSettings?.forceSenderInTitle ?: true,
            effectEnabled = noticeEffectEnabled,
            soundEnabled = noticeSoundEnabled,
            speechEnabled = noticeSpeechEnabled,
            resultText = commandResult?.message,
            onTitleChange = { noticeTitle = it },
            onMessageChange = { noticeMessage = it },
            onEffectEnabledChange = { noticeEffectEnabled = it },
            onSoundEnabledChange = { noticeSoundEnabled = it },
            onSpeechEnabledChange = { noticeSpeechEnabled = it },
            onSend = {
                ConnectionManager.sendNotification(
                    noticeTitle,
                    noticeMessage,
                    noticeEffectEnabled,
                    noticeSoundEnabled,
                    noticeSpeechEnabled,
                )
            },
            onBack = { screen = Screen.Control },
        )

        Screen.Settings -> SettingsScreen(
            onOpenConnection = { screen = Screen.ConnectionSettings },
            onOpenNotifications = { screen = Screen.NotificationSettings },
            onOpenAppearance = { screen = Screen.AppearanceSettings },
            onOpenUpdate = { screen = Screen.Update },
            onOpenDeveloper = { screen = Screen.DeveloperSettings },
            onBack = { screen = Screen.Home },
        )

        Screen.AppearanceSettings -> AppearanceSettingsScreen(
            currentThemeId = settings.themeId,
            onThemeChange = { themeId ->
                settings = settings.copy(themeId = themeId)
                settingsStore.save(settings)
            },
            onBack = { screen = Screen.Settings },
        )

        Screen.ConnectionSettings -> ConnectionSettingsScreen(
            settings = settings,
            stateText = describeConnection(connectionState),
            onSettingsChange = { settings = it; settingsStore.save(it) },
            onReconnect = { settingsStore.save(settings); ConnectionManager.connect(settings) },
            onLogout = { ConnectionManager.logout(settings); screen = Screen.Login },
            onBack = { screen = Screen.Settings },
        )

        Screen.NotificationSettings -> NotificationSettingsScreen(
            settings = settings,
            onSettingsChange = { settings = it; settingsStore.save(it) },
            onBack = { screen = Screen.Settings },
        )

        Screen.DeveloperSettings -> DeveloperSettingsScreen(
            cloudConnectionEnabled = settings.cloudConnectionEnabled,
            lanConnectionEnabled = settings.lanConnectionEnabled,
            onCloudConnectionEnabledChange = { enabled ->
                settings = settings.copy(cloudConnectionEnabled = enabled)
                settingsStore.save(settings)
            },
            onLanConnectionEnabledChange = { enabled ->
                settings = settings.copy(lanConnectionEnabled = enabled)
                settingsStore.save(settings)
            },
            onReconnect = { ConnectionManager.connect(settings) },
            onBack = { screen = Screen.Settings },
        )

        Screen.Update -> UpdateScreen(
            context = context,
            currentVersion = currentVersion,
            serverVersion = connectedServerVersion,
            updateChannel = settings.updateChannel,
            forceUpdate = settings.forceUpdateEnabled,
            onUpdateOptionsChange = { channel, force ->
                settings = settings.copy(updateChannel = channel, forceUpdateEnabled = force)
                settingsStore.save(settings)
            },
            onBack = { screen = Screen.Settings },
        )
        }
    }
}

@Composable
private fun describeConnection(state: ConnectionManager.State): String = when (state) {
    ConnectionManager.State.Idle -> stringResource(R.string.connection_state_idle)
    ConnectionManager.State.Connecting -> stringResource(R.string.connection_state_connecting)
    ConnectionManager.State.LanConnected -> stringResource(R.string.connection_state_lan)
    ConnectionManager.State.CloudConnected -> stringResource(R.string.connection_state_cloud)
    is ConnectionManager.State.Error -> stringResource(R.string.connection_state_error, state.message)
}

/** 排除每秒变化的倒计时/时间戳字段，仅比较状态字段，用于快照落盘节流。 */
private fun snapshotPersistKey(snapshot: ClassStateSnapshot?): String =
    snapshot?.copy(onClassLeftTime = null, onBreakingLeftTime = null, generatedAt = null).toString()

internal fun describeClassState(snapshot: com.remoteci.watch.data.ClassStateSnapshot?): String = when (snapshot?.currentState) {
    Protocol.STATE_CLASS -> "上课"
    Protocol.STATE_PREPARE_CLASS -> "即将上课"
    Protocol.STATE_BREAKING -> "下课"
    Protocol.STATE_AFTER_SCHOOL -> "放学"
    else -> if (homeCourseContent(snapshot).isAvailable) "待机" else "暂无课程"
}
