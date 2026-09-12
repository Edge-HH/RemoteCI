package com.remoteci.watch.ui

import android.content.Context
import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.wear.compose.foundation.pager.VerticalPager
import androidx.wear.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.List
import androidx.compose.material.icons.automirrored.rounded.VolumeOff
import androidx.compose.material.icons.automirrored.rounded.VolumeUp
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Code
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.EditNotifications
import androidx.compose.material.icons.rounded.NotificationsOff
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.School
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.Slider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.rotary.onRotaryScrollEvent
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.wear.compose.foundation.lazy.ScalingLazyColumn
import androidx.wear.compose.foundation.lazy.rememberScalingLazyListState
import androidx.wear.compose.material.CircularProgressIndicator
import androidx.wear.compose.material.Icon
import androidx.wear.compose.material.Switch
import androidx.wear.compose.material.Text
import androidx.wear.compose.material.ToggleChip
import androidx.wear.compose.material.ToggleChipDefaults
import com.remoteci.watch.R
import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.CourseEntry
import com.remoteci.watch.data.ExtensionDefinition
import com.remoteci.watch.data.GitHubAsset
import com.remoteci.watch.data.GitHubRelease
import com.remoteci.watch.data.LanPluginCandidate
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleBundle
import com.remoteci.watch.data.ScheduleDay
import com.remoteci.watch.data.SubjectEntry
import com.remoteci.watch.data.UserProfile
import com.remoteci.watch.data.UpdateManager
import com.remoteci.watch.data.UpdateChannel
import com.remoteci.watch.data.WatchSettings
import java.time.Duration
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal enum class SwapMode { Exchange, Replace }
internal enum class LessonTarget { Source, Target }
internal data class WatchSurfaceLayout(
    val width: Dp,
    val height: Dp,
    val scale: Dp,
    val clipToCircle: Boolean,
)

internal data class HomeInfoChipWidthBounds(
    val minWidth: Dp,
    val maxWidth: Dp,
)

/** 课程按钮与时间框共同按内容伸缩，同时保留圆屏内的安全上下限。 */
internal fun homeInfoChipWidthBounds(diameter: Dp): HomeInfoChipWidthBounds =
    HomeInfoChipWidthBounds(
        minWidth = diameter * .39f,
        maxWidth = diameter * .72f,
    )

/** 圆屏保持圆形安全画布，矩形屏使用完整可用区域。 */
internal fun calculateWatchSurfaceLayout(
    maxWidth: Dp,
    maxHeight: Dp,
    isScreenRound: Boolean,
): WatchSurfaceLayout {
    val diameter = minOf(maxWidth, maxHeight)
    return if (isScreenRound) {
        WatchSurfaceLayout(diameter, diameter, diameter, clipToCircle = true)
    } else {
        WatchSurfaceLayout(maxWidth, maxHeight, diameter, clipToCircle = false)
    }
}

internal data class LessonChoice(
    val id: String,
    val index: Int,
    val periodLabel: String,
    val subject: String,
    val time: String,
    val commandValue: String,
    val enabled: Boolean,
)

/** 当前 M3 配色的快捷入口，所有屏幕统一从主题取色。 */
private val palette: WatchPalette
    @Composable get() = LocalWatchPalette.current

@Composable
internal fun LoginScreen(
    settings: WatchSettings,
    password: String,
    state: ConnectionManager.State,
    onSettingsChange: (WatchSettings) -> Unit,
    onPasswordChange: (String) -> Unit,
    lanPlugins: List<LanPluginCandidate>,
    lanDiscoveryStatus: String?,
    lanDiscoveryScanning: Boolean,
    lanBootstrapPending: LanPluginCandidate?,
    onScanLanPlugins: () -> Unit,
    onSelectLanPlugin: (LanPluginCandidate) -> Unit,
    onConfirmLanBootstrap: () -> Unit,
    onLogin: () -> Unit,
) = WatchList(title = stringResource(R.string.login_title)) {
    item { Input(settings.username, { onSettingsChange(settings.copy(username = it)) }, stringResource(R.string.input_label_username)) }
    item { Input(password, onPasswordChange, stringResource(R.string.input_label_password), password = true) }
    item { Input(settings.cloudServerUrl, { onSettingsChange(settings.copy(cloudServerUrl = it)) }, stringResource(R.string.input_label_cloud_url)) }
    if (settings.cloudServerUrl.trim().startsWith("http://")) {
        item { Hint(stringResource(R.string.cloud_http_warning)) }
    }
    item {
        ActionButton(
            stringResource(if (lanDiscoveryScanning) R.string.lan_scanning else R.string.lan_scan_button),
            Icons.Rounded.Wifi,
            !lanDiscoveryScanning,
            onScanLanPlugins,
        )
    }
    if (!lanDiscoveryStatus.isNullOrBlank()) item { Hint(lanDiscoveryStatus) }
    // 引导返回的云服务器与上次使用的不同：必须再次显式确认（TOFU 强阻断）。
    if (lanBootstrapPending != null) {
        item {
            ActionButton(
                stringResource(R.string.lan_bootstrap_confirm, lanBootstrapPending.instanceName),
                Icons.Rounded.Check,
                true,
                onConfirmLanBootstrap,
            )
        }
    }
    lanPlugins.forEach { plugin ->
        item {
            ActionButton(
                "${plugin.instanceName} · ${plugin.host}:${plugin.port}",
                Icons.Rounded.Wifi,
                !lanDiscoveryScanning,
                onClick = { onSelectLanPlugin(plugin) },
            )
        }
    }
    item { ActionButton(stringResource(R.string.login_button), Icons.Rounded.Wifi, settings.username.isNotBlank() && password.length >= 8, onLogin) }
    item { Hint(describeConnectionForScreen(state)) }
}

@Composable
internal fun HomeScreen(
    connectionState: ConnectionManager.State,
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    capabilities: Set<String> = Protocol.BASELINE_CAPABILITIES,
    onOpenScheduleOverview: () -> Unit,
    onOpenScheduleChange: () -> Unit,
    onQuickSwapCourse: (() -> Unit)?,
    onQuickSwapNextCourse: (() -> Unit)?,
    onOpenNotification: () -> Unit,
    onOpenSettings: () -> Unit,
    onRetryConnection: () -> Unit,
) {
    var now by remember(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes) {
        mutableStateOf(pluginLocalNow(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, 0L, 0L))
    }
    // 以插件快照的 UTC 生成时间为基准推算“插件本地当前时间”，避免两端时区/时钟不一致时进度环为空。
    LaunchedEffect(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes) {
        val baseElapsed = SystemClock.elapsedRealtime()
        while (true) {
            now = pluginLocalNow(
                snapshot?.generatedAt,
                snapshot?.timeZoneOffsetMinutes,
                baseElapsed,
                SystemClock.elapsedRealtime(),
            )
            delay(30_000)
        }
    }
    WatchSurface { diameter ->
        val pagerState = rememberPagerState(pageCount = { 2 })
        Box(Modifier.fillMaxSize()) {
            // Wear Compose 的 VerticalPager 默认内置表冠/旋转表圈支持：
            // 一格贴合一页、自动管理旋转焦点，并按设备分辨率（表冠/表圈）自适应灵敏度。
            VerticalPager(
                state = pagerState,
                modifier = Modifier.fillMaxSize(),
            ) { page ->
                if (page == 0) {
                    HomeStatusPage(
                        connectionState = connectionState,
                        snapshot = snapshot,
                        user = user,
                        now = now,
                        diameter = diameter,
                        onQuickSwapCourse = onQuickSwapCourse,
                        onQuickSwapNextCourse = onQuickSwapNextCourse,
                        onRetryConnection = onRetryConnection,
                    )
                } else {
                    HomeMenuPage(
                        user = user,
                        capabilities = capabilities,
                        onOpenScheduleOverview = onOpenScheduleOverview,
                        onOpenScheduleChange = onOpenScheduleChange,
                        onOpenNotification = onOpenNotification,
                        onOpenSettings = onOpenSettings,
                    )
                }
            }
            HomePageIndicator(
                currentPage = pagerState.currentPage,
                diameter = diameter,
                modifier = Modifier.align(Alignment.CenterEnd),
            )
        }
    }
}

@Composable
private fun HomeStatusPage(
    connectionState: ConnectionManager.State,
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    now: LocalTime,
    diameter: Dp,
    onQuickSwapCourse: (() -> Unit)?,
    onQuickSwapNextCourse: (() -> Unit)?,
    onRetryConnection: () -> Unit,
) {
    // 下课和即将上课时，主页的主课程入口代表即将发生的下一节课；
    // 文案、时间和点击后的换课源课必须始终来自同一个目标，避免“显示数学却换了语文”。
    val courseContent = homeCourseContent(snapshot)
    val infoChipWidth = homeInfoChipWidthBounds(diameter)
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(palette.homePanel)
            .clickable(
                enabled = connectionState is ConnectionManager.State.Error,
                onClick = onRetryConnection,
            ),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(diameter * .26f))
        Row(
            modifier = Modifier.height(diameter * .13f),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.Center,
        ) {
            if (shouldShowStateProgress(snapshot)) {
                CircularProgressIndicator(
                    progress = lessonProgress(snapshot?.currentTimeLayoutItem, now),
                    modifier = Modifier.size(diameter * .125f),
                    strokeWidth = diameter * .02f,
                    indicatorColor = palette.progressActive,
                    trackColor = palette.progressTrack,
                )
                Spacer(Modifier.width(diameter * .04f))
            }
            Text(
                describeClassState(snapshot),
                color = Color.Black,
                fontSize = 22.sp,
                fontWeight = FontWeight.Bold,
            )
        }
        Spacer(Modifier.height(diameter * .10f))
        // 先按两个控件的固有宽度取较大值，再让它们填满同一容器，保证始终等宽。
        Column(
            modifier = Modifier
                .widthIn(min = infoChipWidth.minWidth, max = infoChipWidth.maxWidth)
                .width(IntrinsicSize.Max),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            if (courseContent.isAvailable) {
                HomeInfoChip(
                    text = courseContent.subject,
                    icon = Icons.Rounded.SwapHoriz,
                    filled = true,
                    modifier = Modifier.fillMaxWidth().height(diameter * .14f),
                    fontSize = if (courseContent.subject.length <= 2) 16.sp else 12.sp,
                    iconSize = diameter * .055f,
                    onClick = onQuickSwapCourse,
                )
                Spacer(Modifier.height(diameter * .035f))
            }
            HomeInfoChip(
                text = extractTimeRange(courseContent.timeLayoutItem).ifBlank { "--:--" },
                filled = false,
                modifier = Modifier.fillMaxWidth().height(diameter * .105f),
                fontSize = 12.sp,
            )
        }
        if (canViewExtendedSchedule(user) && shouldShowNextLessonSummary(snapshot?.currentState)) {
            Spacer(Modifier.height(diameter * .035f))
            val nextSubject = snapshot?.nextClassSubject?.trim().orEmpty()
            if (shouldHighlightNextLessonAction(snapshot) && onQuickSwapNextCourse != null) {
                Row(
                    modifier = Modifier.widthIn(max = diameter * .78f),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.Center,
                ) {
                    Text(
                        stringResource(R.string.home_next_lesson_prefix),
                        color = Color.Black,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                    )
                    Text(
                        nextSubject,
                        modifier = Modifier
                            .weight(1f, fill = false)
                            .clip(RoundedCornerShape(9.dp))
                            .border(1.dp, palette.progressActive, RoundedCornerShape(9.dp))
                            .clickable(onClick = onQuickSwapNextCourse)
                            .padding(horizontal = 7.dp, vertical = 3.dp),
                        color = Color.Black,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            } else {
                Text(
                    stringResource(
                        R.string.home_next_lesson,
                        snapshot?.nextClassSubject ?: stringResource(R.string.home_next_lesson_none),
                    ),
                    color = Color.Black,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun HomeMenuPage(
    user: UserProfile?,
    capabilities: Set<String>,
    onOpenScheduleOverview: () -> Unit,
    onOpenScheduleChange: () -> Unit,
    onOpenNotification: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    val actions = homeActionLabels(user, capabilities).map { label ->
        when (label) {
            "课表" -> HomeAction(label, Icons.AutoMirrored.Rounded.List, onOpenScheduleOverview)
            "换课" -> HomeAction(label, Icons.Rounded.SwapHoriz, onOpenScheduleChange)
            "控制" -> HomeAction(label, Icons.Rounded.Wifi, onOpenNotification)
            else -> HomeAction(label, Icons.Rounded.Settings, onOpenSettings)
        }
    }
    // 首页菜单不显示“菜单”标题，让选项直接占满一屏，避免旋转翻页后还要滚动。
    WatchList(title = stringResource(R.string.home_menu_title), showTitle = false) {
        actions.forEach { action ->
            item { ActionButton(action.label, action.icon, true, action.onClick) }
        }
    }
}

@Composable
private fun HomePageIndicator(currentPage: Int, diameter: Dp, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(end = diameter * .035f),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        repeat(2) { page ->
            Box(
                Modifier
                    .size(9.dp)
                    .clip(CircleShape)
                    .background(if (currentPage == page) palette.primaryContainer else Color.White.copy(alpha = .92f)),
            )
        }
    }
}

@Composable
internal fun ScheduleOverviewScreen(
    day: ScheduleDay?,
    today: LocalDate,
    connectionReady: Boolean,
    pullState: ConnectionManager.SchedulePullState,
    onRequestSchedule: () -> Unit,
    onPickDate: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.schedule_title)) {
    if (day == null) {
        item { Hint(stringResource(R.string.schedule_not_synced)) }
    } else {
        // 日期标题同时承担切换入口，单页始终只渲染一天，避免七日内容在圆屏上过长。
        item { ActionButton(scheduleDateTitle(day.date, today), null, true, onPickDate) }
        if (day.courses.isEmpty()) item { Hint(stringResource(R.string.schedule_empty_day)) }
        day.courses.forEach { course ->
            item { LessonSummary(course) }
        }
    }

    when (pullState) {
        is ConnectionManager.SchedulePullState.Pulling -> {
            item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
            item { Hint(pullState.message) }
        }
        is ConnectionManager.SchedulePullState.Success -> item { Hint(pullState.message) }
        is ConnectionManager.SchedulePullState.Error -> item { Hint(pullState.message) }
        ConnectionManager.SchedulePullState.Idle -> Unit
    }
    if (shouldOfferSchedulePull(day, connectionReady)) {
        val pulling = !schedulePullActionEnabled(pullState)
        item {
            ActionButton(
                stringResource(if (pulling) R.string.schedule_pulling_button else R.string.schedule_pull_button),
                Icons.Rounded.Refresh,
                !pulling,
                onRequestSchedule,
            )
        }
    }
    item { BackButton(onBack) }
}

/** 无论本地是否已有缓存，只要在线就允许强制拉取并覆盖旧课表。 */
internal fun shouldOfferSchedulePull(day: ScheduleDay?, connectionReady: Boolean): Boolean = connectionReady

/** 任一端的课表任务正在运行时，本端按钮禁用，避免重复提交。 */
internal fun schedulePullActionEnabled(state: ConnectionManager.SchedulePullState): Boolean =
    state !is ConnectionManager.SchedulePullState.Pulling

@Composable
internal fun ScheduleDatePickerScreen(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate,
    onSelect: (ScheduleDay) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.pick_date_title)) {
    val days = availableScheduleDays(bundle, afterSchool, today)
    if (days.isEmpty()) item { Hint(stringResource(R.string.no_viewable_schedule)) }
    days.forEach { day ->
        item { ActionButton(scheduleDateTitle(day.date, today), null, true, onClick = { onSelect(day) }) }
    }
    item { BackButton(onBack) }
}

@Composable
internal fun DayPickerScreen(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate,
    onSelect: (ScheduleDay) -> Unit,
    onBack: () -> Unit,
) =
    WatchList(title = stringResource(R.string.pick_date_title)) {
        val days = availableScheduleDays(bundle, afterSchool, today)
        if (days.isEmpty()) item { Hint(stringResource(R.string.no_swappable_day)) }
        days.forEach { day ->
            item { ActionButton(scheduleDateTitle(day.date, today), null, day.enabled, onClick = { onSelect(day) }) }
        }
        item { BackButton(onBack) }
    }

@Composable
internal fun SwapScreen(
    day: ScheduleDay?,
    mode: SwapMode,
    sourceLesson: LessonChoice?,
    targetLesson: LessonChoice?,
    replacementSubject: String?,
    connectionReady: Boolean,
    resultText: String?,
    onModeChange: (SwapMode) -> Unit,
    onPickSource: () -> Unit,
    onPickTarget: () -> Unit,
    onSubmit: () -> Unit,
) = WatchList(title = day?.date ?: stringResource(R.string.swap_title)) {
    item { ModeSelector(mode, onModeChange) }
    item { LessonButton(stringResource(R.string.swap_source_label), sourceLesson?.subject ?: stringResource(R.string.pick_placeholder), onPickSource) }
    item { LessonButton(stringResource(R.string.swap_target_label), if (mode == SwapMode.Exchange) targetLesson?.subject ?: stringResource(R.string.pick_placeholder) else replacementSubject ?: stringResource(R.string.pick_subject_placeholder), onPickTarget) }
    val validTarget = if (mode == SwapMode.Exchange) targetLesson != null && targetLesson.index != sourceLesson?.index else replacementSubject != null
    item { ActionButton(stringResource(if (mode == SwapMode.Exchange) R.string.confirm_exchange else R.string.confirm_replace), Icons.Rounded.Check, connectionReady && sourceLesson != null && validTarget, onSubmit) }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
}

@Composable
internal fun LessonPickerScreen(
    title: String,
    lessons: List<LessonChoice>,
    selectedIndex: Int?,
    excludedIndex: Int?,
    onSelect: (LessonChoice) -> Unit,
) = WatchList(title = title) {
    lessons.forEach { lesson -> item {
        ActionButton(
            "${if (lesson.index == selectedIndex) "✓ " else ""}${lesson.periodLabel} ${lesson.subject}",
            null,
            lesson.enabled && lesson.index != excludedIndex,
            onClick = { onSelect(lesson) },
        )
    } }
}

@Composable
internal fun SubjectPickerScreen(
    subjects: List<SubjectEntry>,
    selectedSubjectId: String?,
    onSelect: (SubjectEntry) -> Unit,
) = WatchList(title = stringResource(R.string.pick_subject_title)) {
    subjects.forEach { subject -> item {
        ActionButton("${if (subject.id == selectedSubjectId) "✓ " else ""}${subject.name}", null, true, onClick = { onSelect(subject) })
    } }
}

@Composable
internal fun ControlScreen(
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    capabilities: Set<String> = Protocol.BASELINE_CAPABILITIES,
    extensions: List<ExtensionDefinition>,
    resultText: String?,
    onTeacherComing: () -> Unit,
    onOpenNotification: () -> Unit,
    onOpenVoiceMessage: () -> Unit,
    onClearNotifications: () -> Unit,
    onToggleMainMenu: () -> Unit,
    onOpenVolume: () -> Unit,
    onOpenPower: () -> Unit,
    onRunExtension: (ExtensionDefinition) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.control_title)) {
    val canTeacherComing = user?.has(Protocol.PERMISSION_TEACHER_COMING) == true && Protocol.CAP_TEACHER_COMING in capabilities
    val canNotify = user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true && Protocol.CAP_NOTIFICATION_SEND in capabilities
    val canClearNotifications = user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true && Protocol.CAP_NOTIFICATION_CLEAR in capabilities
    val canControlMainMenu = user?.has(Protocol.PERMISSION_MAIN_MENU_CONTROL) == true && Protocol.CAP_MAIN_MENU_VISIBILITY in capabilities
    val canControlPower = user?.has(Protocol.PERMISSION_POWER_CONTROL) == true && Protocol.CAP_POWER_CONTROL in capabilities
    val canControlVolume = user?.has(Protocol.PERMISSION_POWER_CONTROL) == true && Protocol.CAP_VOLUME_CONTROL in capabilities
    if (canTeacherComing) item { ActionButton(stringResource(R.string.teacher_coming), Icons.Rounded.School, true, onTeacherComing) }
    if (canNotify) item { ActionButton(stringResource(R.string.send_notification), Icons.Rounded.EditNotifications, true, onOpenNotification) }
    if (user?.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) == true && Protocol.CAP_VOICE_MESSAGE_SEND in capabilities)
        item { ActionButton("发送语音", Icons.Rounded.EditNotifications, true, onOpenVoiceMessage) }
    if (canClearNotifications && shouldShowClearNotifications(snapshot)) item {
        ActionButton(stringResource(R.string.clear_notifications), Icons.Rounded.NotificationsOff, true, onClearNotifications)
    }
    if (canControlMainMenu) item {
        ActionButton(
            mainMenuActionLabel(snapshot),
            if (snapshot?.isMainMenuVisible == false) Icons.Rounded.Visibility else Icons.Rounded.VisibilityOff,
            canControlMainMenu,
            onToggleMainMenu,
        )
    }
    if (canControlVolume) item {
        ActionButton(
            stringResource(R.string.volume_title),
            if (snapshot?.isMuted == true) Icons.AutoMirrored.Rounded.VolumeOff else Icons.AutoMirrored.Rounded.VolumeUp,
            snapshot?.isVolumeControlAvailable == true,
            onOpenVolume,
        )
    }
    if (canControlPower) item { ActionButton(stringResource(R.string.power_title), Icons.Rounded.PowerSettingsNew, true, onOpenPower) }
    // 扩展入口同时遵守独立扩展权限、服务端策略和账号自己的展示偏好。
    if (Protocol.CAP_EXTENSIONS_RUN in capabilities) visibleExtensionsFor(user, extensions).forEach { extension ->
        item {
            ActionButton(
                extension.displayName,
                extensionIcon(extension.icon),
                enabled = true,
                onClick = { onRunExtension(extension) },
            )
        }
    }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
    item { BackButton(onBack) }
}

internal fun shouldShowClearNotifications(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.isNotificationPlaying == true

internal fun mainMenuActionLabel(snapshot: ClassStateSnapshot?): String =
    if (snapshot?.isMainMenuVisible == false) "显示主菜单" else "隐藏主菜单"

/** 扩展功能按服务端策略和当前用户偏好过滤；隐藏按钮不构成安全控制，插件执行端会再次校验。 */
internal fun visibleExtensionsFor(
    user: UserProfile?,
    extensions: List<ExtensionDefinition>,
): List<ExtensionDefinition> = extensions.filter { user?.showsOnWatch(it) == true }

/** 按参数 schema 生成初始表单值；switch 默认 false，其余使用注册的默认值。 */
internal fun defaultExtensionArgs(extension: ExtensionDefinition): Map<String, String?> =
    extension.parameters.associate { param ->
        param.key to when (param.type) {
            Protocol.EXT_PARAM_SWITCH -> (param.defaultValue ?: "false").let { if (it == "true") "true" else "false" }
            else -> param.defaultValue
        }
    }

/** select 参数点击后的下一个候选项（循环切换；当前值不在候选中时从第一项开始）。 */
internal fun nextSelectValue(options: List<String>, current: String?): String {
    if (options.isEmpty()) return current ?: ""
    return options[(options.indexOf(current) + 1).mod(options.size)]
}

/** 扩展参数输入页：按注册的 schema 渲染字段，提交后由调用方发送 RunExtension 命令。 */
@Composable
internal fun ExtensionFormScreen(
    extension: ExtensionDefinition,
    connectionReady: Boolean,
    resultText: String?,
    onSubmit: (Map<String, String?>) -> Unit,
    onBack: () -> Unit,
) {
    var values by remember(extension.id) { mutableStateOf(defaultExtensionArgs(extension)) }
    WatchList(title = extension.displayName) {
        extension.parameters.forEach { param -> item {
            when (param.type) {
                Protocol.EXT_PARAM_SWITCH -> Toggle(param.label, values[param.key] == "true") {
                    values = values + (param.key to it.toString())
                }

                Protocol.EXT_PARAM_SELECT -> ActionButton(
                    "${param.label}：${values[param.key] ?: ""}",
                    null,
                    param.options.isNotEmpty(),
                    onClick = { values = values + (param.key to nextSelectValue(param.options, values[param.key])) },
                )

                else -> Input(
                    values[param.key] ?: "",
                    { values = values + (param.key to it) },
                    param.label,
                )
            }
        } }
        item { ActionButton(stringResource(R.string.run_extension), Icons.Rounded.Check, connectionReady, onClick = { onSubmit(values) }) }
        if (!resultText.isNullOrBlank()) item { Hint(resultText) }
        item { BackButton(onBack) }
    }
}

@Composable
internal fun VolumeScreen(
    volumePercent: Int,
    muted: Boolean,
    available: Boolean,
    onVolumeChange: (Int) -> Unit,
    onMutedChange: (Boolean) -> Unit,
    onBack: () -> Unit,
) {
    var localVolume by remember(volumePercent) { mutableStateOf(volumePercent.coerceIn(0, 100)) }
    val focusRequester = remember { FocusRequester() }
    LaunchedEffect(Unit) { focusRequester.requestFocus() }

    WatchList(title = stringResource(R.string.volume_title)) {
        item { Hint(stringResource(if (muted) R.string.volume_muted_hint else R.string.volume_hint, localVolume)) }
        item {
            Slider(
                value = localVolume.toFloat(),
                onValueChange = { localVolume = it.toInt().coerceIn(0, 100) },
                onValueChangeFinished = { onVolumeChange(localVolume) },
                valueRange = 0f..100f,
                enabled = available,
                modifier = Modifier.fillMaxWidth(.78f)
                    .focusRequester(focusRequester)
                    .onRotaryScrollEvent { event ->
                        val adjusted = adjustVolumeForRotary(localVolume, event.verticalScrollPixels)
                        if (adjusted != localVolume) {
                            localVolume = adjusted
                            onVolumeChange(adjusted)
                        }
                        true
                    }
                    .focusable(),
            )
        }
        item { Hint(stringResource(R.string.volume_rotary_hint)) }
        item {
            ActionButton(
                stringResource(if (muted) R.string.unmute else R.string.mute),
                if (muted) Icons.AutoMirrored.Rounded.VolumeUp else Icons.AutoMirrored.Rounded.VolumeOff,
                available,
                onClick = { onMutedChange(!muted) },
            )
        }
        item { BackButton(onBack) }
    }
}

internal fun adjustVolumeForRotary(current: Int, verticalScrollPixels: Float): Int {
    val step = when {
        verticalScrollPixels > 0 -> 2
        verticalScrollPixels < 0 -> -2
        else -> 0
    }
    return (current + step).coerceIn(0, 100)
}

@Composable
internal fun PowerScreen(
    sleepAvailable: Boolean,
    hibernateAvailable: Boolean,
    onPowerAction: (Int) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.power_title)) {
    item { ActionButton(stringResource(R.string.power_shutdown), Icons.Rounded.PowerSettingsNew, true, onClick = { onPowerAction(Protocol.POWER_SHUTDOWN) }) }
    item { ActionButton(stringResource(R.string.power_restart), Icons.Rounded.RestartAlt, true, onClick = { onPowerAction(Protocol.POWER_RESTART) }) }
    item { ActionButton(stringResource(R.string.power_sleep), Icons.Rounded.PowerSettingsNew, sleepAvailable, onClick = { onPowerAction(Protocol.POWER_SLEEP) }) }
    if (hibernateAvailable) item {
        ActionButton(stringResource(R.string.power_hibernate), Icons.Rounded.PowerSettingsNew, true, onClick = { onPowerAction(Protocol.POWER_HIBERNATE) })
    }
    item { BackButton(onBack) }
}

@Composable
internal fun NotificationScreen(
    title: String,
    message: String,
    forceSenderInTitle: Boolean,
    effectEnabled: Boolean,
    soundEnabled: Boolean,
    speechEnabled: Boolean,
    resultText: String?,
    onTitleChange: (String) -> Unit,
    onMessageChange: (String) -> Unit,
    onEffectEnabledChange: (Boolean) -> Unit,
    onSoundEnabledChange: (Boolean) -> Unit,
    onSpeechEnabledChange: (Boolean) -> Unit,
    onSend: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.notice_screen_title)) {
    item { Input(title, onTitleChange, stringResource(R.string.notice_title_label)) }
    if (forceSenderInTitle) item { Hint(stringResource(R.string.notice_sender_hint)) }
    item { Input(message, onMessageChange, stringResource(R.string.notice_message_label)) }
    item { Toggle(stringResource(R.string.notice_effect_toggle), effectEnabled, onEffectEnabledChange) }
    item { Toggle(stringResource(R.string.notice_sound_toggle), soundEnabled, onSoundEnabledChange) }
    item { Toggle(stringResource(R.string.notice_speech_toggle), speechEnabled, onSpeechEnabledChange) }
    // 标题留空时由插件使用默认标题；正文可留空，此时 ClassIsland 只显示标题。
    item { ActionButton(stringResource(R.string.notice_send_button), Icons.Rounded.EditNotifications, true, onSend) }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
    item { BackButton(onBack) }
}

@Composable
internal fun SettingsScreen(
    onOpenConnection: () -> Unit,
    onOpenNotifications: () -> Unit,
    onOpenAppearance: () -> Unit,
    onOpenUpdate: () -> Unit,
    onOpenDeveloper: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.settings_title)) {
    item { ActionButton(stringResource(R.string.settings_connection), Icons.Rounded.Wifi, true, onOpenConnection) }
    item { ActionButton(stringResource(R.string.settings_notifications), Icons.Rounded.EditNotifications, true, onOpenNotifications) }
    item { ActionButton(stringResource(R.string.settings_appearance), Icons.Rounded.Palette, true, onOpenAppearance) }
    item { ActionButton(stringResource(R.string.settings_update), Icons.Rounded.SystemUpdate, true, onOpenUpdate) }
    item { ActionButton(stringResource(R.string.settings_developer), Icons.Rounded.Code, true, onOpenDeveloper) }
    item { BackButton(onBack) }
}

@Composable
internal fun AppearanceSettingsScreen(
    currentThemeId: String,
    onThemeChange: (String) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.settings_appearance)) {
    item { Hint(stringResource(R.string.appearance_pick_palette)) }
    WatchPalette.All.forEach { option ->
        item {
            ThemeOptionButton(
                option = option,
                selected = option.id == currentThemeId,
                onClick = { onThemeChange(option.id) },
            )
        }
    }
    item { BackButton(onBack) }
}

/** 外观设置页的配色选项：色点 + 名称 + 当前选中标记。 */
@Composable
private fun ThemeOptionButton(
    option: WatchPalette,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(.78f).height(38.dp).clip(RoundedCornerShape(19.dp))
            .background(if (selected) palette.buttonContainer else Color.Transparent)
            .then(if (selected) Modifier else Modifier.border(.5.dp, Color.White.copy(.35f), RoundedCornerShape(19.dp)))
            .clickable(onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(14.dp).clip(CircleShape).background(option.progressActive))
        Spacer(Modifier.width(8.dp))
        Text(
            option.label,
            color = if (selected) palette.onButtonContainer else Color.White,
            fontSize = 12.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        if (selected) {
            Spacer(Modifier.width(6.dp))
            Icon(Icons.Rounded.Check, null, tint = palette.onButtonContainer, modifier = Modifier.size(15.dp))
        }
    }
}

/** 更新页状态。 */
private sealed interface UpdateUiState {
    data object Idle : UpdateUiState
    data object Checking : UpdateUiState
    data class UpToDate(val message: String) : UpdateUiState
    data object Downloading : UpdateUiState
    data object Installing : UpdateUiState
    data class Available(
        val latestVersion: String,
        val release: GitHubRelease,
        val asset: GitHubAsset,
    ) : UpdateUiState

    data class Error(val message: String) : UpdateUiState
}

@Composable
internal fun UpdateScreen(
    context: Context,
    currentVersion: String,
    serverVersion: String?,
    updateChannel: UpdateChannel,
    forceUpdate: Boolean,
    onUpdateOptionsChange: (UpdateChannel, Boolean) -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var state by remember { mutableStateOf<UpdateUiState>(UpdateUiState.Idle) }

    fun checkUpdate(): Unit {
        if (serverVersion.isNullOrBlank()) {
            state = UpdateUiState.Error(context.getString(R.string.update_need_webui))
            return
        }
        state = UpdateUiState.Checking
        scope.launch {
            state = try {
                val selected = UpdateManager.selectCompatibleUpdate(
                    releases = UpdateManager.fetchReleases(),
                    currentVersion = currentVersion,
                    channel = updateChannel,
                    force = forceUpdate,
                )
                if (selected != null) {
                    UpdateUiState.Available(
                        UpdateManager.versionFromTag(selected.release.tagName),
                        selected.release,
                        selected.asset,
                    )
                } else {
                    UpdateUiState.UpToDate(context.getString(R.string.update_up_to_date, currentVersion))
                }
            } catch (error: Exception) {
                UpdateUiState.Error(error.message ?: context.getString(R.string.update_check_failed))
            }
        }
    }

    WatchList(title = stringResource(R.string.settings_update)) {
        item { Hint(stringResource(R.string.update_current_version, currentVersion)) }
        item { Hint(stringResource(R.string.update_connected_webui, serverVersion ?: stringResource(R.string.update_webui_unknown))) }
        item { Hint(stringResource(R.string.update_channel_label)) }
        item {
            UpdateChannelSelector(updateChannel) { channel ->
                state = UpdateUiState.Idle
                onUpdateOptionsChange(channel, forceUpdate)
            }
        }
        item {
            Toggle(stringResource(R.string.update_force_toggle), forceUpdate) { enabled ->
                state = UpdateUiState.Idle
                onUpdateOptionsChange(updateChannel, enabled)
            }
        }
        if (forceUpdate) item { Hint(stringResource(R.string.update_force_hint)) }
        when (val current = state) {
            UpdateUiState.Idle, UpdateUiState.Checking -> {
                if (current == UpdateUiState.Checking) {
                    item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
                }
                item {
                    ActionButton(
                        stringResource(R.string.update_check_button),
                        Icons.Rounded.SystemUpdate,
                        enabled = current != UpdateUiState.Checking,
                        onClick = ::checkUpdate,
                    )
                }
            }

            is UpdateUiState.UpToDate -> {
                item { Hint(current.message) }
                item {
                    ActionButton(stringResource(R.string.update_recheck_button), Icons.Rounded.SystemUpdate, true, onClick = ::checkUpdate)
                }
            }

            is UpdateUiState.Available -> {
                val reinstalling = UpdateManager.compareVersions(current.latestVersion, currentVersion) == 0
                item { Hint(stringResource(if (reinstalling) R.string.update_reinstall_available else R.string.update_new_version, current.latestVersion)) }
                item { Hint(releaseNotesPreview(current.release.body)) }
                item {
                    ActionButton(stringResource(if (reinstalling) R.string.update_reinstall_button else R.string.update_install_button), Icons.Rounded.Download, true, onClick = {
                        state = UpdateUiState.Downloading
                        scope.launch {
                            state = try {
                                serverVersion
                                    ?: throw IllegalStateException("WebUI 连接已断开，请重新检查更新")
                                val apk = UpdateManager.downloadApk(context, current.asset)
                                // 下载期间连接可能断开，安装前再次确认仍连接着协议 V3 服务端。
                                ConnectionManager.serverVersion.value
                                    ?: throw IllegalStateException("WebUI 连接已断开，请重新检查更新")
                                UpdateManager.installApk(context, apk)
                                UpdateUiState.Installing
                            } catch (error: Exception) {
                                UpdateUiState.Error(error.message ?: context.getString(R.string.update_failed))
                            }
                        }
                    })
                }
            }

            UpdateUiState.Downloading -> {
                item { Hint(stringResource(R.string.update_downloading)) }
                item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
            }

            UpdateUiState.Installing -> {
                item { Hint(stringResource(R.string.update_installing)) }
                item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
            }

            is UpdateUiState.Error -> {
                item { Hint(current.message) }
                item {
                    ActionButton(stringResource(R.string.update_recheck_button), Icons.Rounded.SystemUpdate, true, onClick = ::checkUpdate)
                }
            }
        }
        item { BackButton(onBack) }
    }
}

@Composable
private fun UpdateChannelSelector(channel: UpdateChannel, onChange: (UpdateChannel) -> Unit) {
    Row(Modifier.width(150.dp).height(30.dp).clip(RoundedCornerShape(15.dp)).background(palette.disabledContainer)) {
        listOf(UpdateChannel.STABLE to stringResource(R.string.update_channel_stable), UpdateChannel.BETA to stringResource(R.string.update_channel_beta)).forEach { (value, label) ->
            Box(
                Modifier.weight(1f).fillMaxSize()
                    .background(if (channel == value) palette.buttonContainer else Color.Transparent)
                    .clickable { onChange(value) },
                contentAlignment = Alignment.Center,
            ) {
                Text(label, color = if (channel == value) palette.onButtonContainer else Color.White, fontSize = 11.sp)
            }
        }
    }
}

/** 截取 release 说明供手表小屏展示。 */
private fun releaseNotesPreview(body: String?, maxLength: Int = 180): String {
    if (body.isNullOrBlank()) return "暂无更新说明"
    val plain = body.replace("\r", "").trim()
    return if (plain.length <= maxLength) plain else plain.take(maxLength) + "…"
}

@Composable
internal fun ConnectionSettingsScreen(
    settings: WatchSettings,
    stateText: String,
    onSettingsChange: (WatchSettings) -> Unit,
    onReconnect: () -> Unit,
    onLogout: () -> Unit,
    onBack: () -> Unit,
) {
    var portText by remember(settings.lanPort) { mutableStateOf(settings.lanPort.toString()) }
    WatchList(title = stringResource(R.string.settings_connection)) {
        item { Hint(stateText) }
        item { Input(settings.cloudServerUrl, { onSettingsChange(settings.copy(cloudServerUrl = it)) }, stringResource(R.string.input_label_cloud_url)) }
        item { Input(settings.lanHost, { onSettingsChange(settings.copy(lanHost = it)) }, stringResource(R.string.lan_host_label)) }
        item {
            Input(portText, { value ->
                portText = value
                parseLanPort(value)?.let { onSettingsChange(settings.copy(lanPort = it)) }
            }, stringResource(R.string.lan_port_label))
        }
        item { ActionButton(stringResource(R.string.save_and_reconnect), Icons.Rounded.Wifi, true, onReconnect) }
        item { ActionButton(stringResource(R.string.logout), null, true, onLogout, subtle = true) }
        item { BackButton(onBack) }
    }
}

@Composable
internal fun DeveloperSettingsScreen(
    cloudConnectionEnabled: Boolean,
    lanConnectionEnabled: Boolean,
    onCloudConnectionEnabledChange: (Boolean) -> Unit,
    onLanConnectionEnabledChange: (Boolean) -> Unit,
    onReconnect: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.settings_developer)) {
    item { Hint(stringResource(R.string.developer_hint)) }
    if (shouldShowCloudConnectionToggle(developerSettings = true)) {
        item { Toggle(stringResource(R.string.cloud_server_toggle), cloudConnectionEnabled, onCloudConnectionEnabledChange) }
    }
    if (shouldShowLanConnectionToggle(developerSettings = true)) {
        item { Toggle(stringResource(R.string.lan_direct_toggle), lanConnectionEnabled, onLanConnectionEnabledChange) }
    }
    item { ActionButton(stringResource(R.string.apply_and_reconnect), Icons.Rounded.Wifi, true, onReconnect) }
    item { BackButton(onBack) }
}

internal fun shouldShowCloudConnectionToggle(developerSettings: Boolean): Boolean = developerSettings
internal fun shouldShowLanConnectionToggle(developerSettings: Boolean): Boolean = developerSettings
internal fun parseLanPort(value: String): Int? = value.trim().toIntOrNull()?.takeIf { it in 1..65535 }

@Composable
internal fun NotificationSettingsScreen(
    settings: WatchSettings,
    onSettingsChange: (WatchSettings) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = stringResource(R.string.settings_notifications)) {
    item { Toggle(stringResource(R.string.notif_on_class), settings.receiveOnClass) { onSettingsChange(settings.copy(receiveOnClass = it)) } }
    item { Toggle(stringResource(R.string.notif_on_breaking), settings.receiveOnBreaking) { onSettingsChange(settings.copy(receiveOnBreaking = it)) } }
    item { Toggle(stringResource(R.string.notif_after_school), settings.receiveAfterSchool) { onSettingsChange(settings.copy(receiveAfterSchool = it)) } }
    item { Toggle(stringResource(R.string.notif_schedule_changed), settings.receiveScheduleChanged) { onSettingsChange(settings.copy(receiveScheduleChanged = it)) } }
    item { Toggle(stringResource(R.string.notif_custom), settings.receiveCustom) { onSettingsChange(settings.copy(receiveCustom = it)) } }
    item { Toggle(stringResource(R.string.notif_automation), settings.receiveAutomationNotifications) { onSettingsChange(settings.copy(receiveAutomationNotifications = it)) } }
    item { Toggle(stringResource(R.string.notif_plugin), settings.receivePluginNotifications) { onSettingsChange(settings.copy(receivePluginNotifications = it)) } }
    item { BackButton(onBack) }
}

@Composable
internal fun WatchList(
    title: String,
    showTitle: Boolean = true,
    content: androidx.wear.compose.foundation.lazy.ScalingLazyListScope.() -> Unit,
) {
    WatchSurface {
        ScalingLazyColumn(
            modifier = Modifier.fillMaxSize(),
            state = rememberScalingLazyListState(),
            horizontalAlignment = Alignment.CenterHorizontally,
            contentPadding = PaddingValues(top = 34.dp, bottom = 58.dp),
        ) {
            if (showTitle) {
                item { Text(title, color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold) }
                item { Spacer(Modifier.height(6.dp)) }
            }
            content()
        }
    }
}

@Composable
private fun WatchSurface(content: @Composable BoxScope.(Dp) -> Unit) {
    val isScreenRound = LocalConfiguration.current.isScreenRound
    BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black), contentAlignment = Alignment.Center) {
        val layout = calculateWatchSurfaceLayout(maxWidth, maxHeight, isScreenRound)
        val surfaceModifier = Modifier.size(layout.width, layout.height)
            .then(if (layout.clipToCircle) Modifier.clip(CircleShape) else Modifier)
            .background(Color.Black)
        Box(surfaceModifier, contentAlignment = Alignment.Center) { content(layout.scale) }
    }
}

@Composable
internal fun ActionButton(
    label: String,
    icon: ImageVector?,
    enabled: Boolean,
    onClick: () -> Unit,
    subtle: Boolean = false,
) {
    Row(
        modifier = Modifier.fillMaxWidth(.78f).height(38.dp).clip(RoundedCornerShape(19.dp))
            .background(if (!enabled) palette.disabledContainer else if (subtle) Color.Transparent else palette.buttonContainer)
            .then(if (subtle) Modifier.border(.5.dp, Color.White.copy(.35f), RoundedCornerShape(19.dp)) else Modifier)
            .clickable(enabled = enabled, onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) { Icon(icon, null, tint = if (subtle) Color.White else palette.onButtonContainer, modifier = Modifier.size(17.dp)); Spacer(Modifier.width(5.dp)) }
        Text(label, color = if (!enabled) palette.disabledContent else if (subtle) Color.White else palette.onButtonContainer, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable private fun BackButton(onClick: () -> Unit) = ActionButton(stringResource(R.string.back), Icons.AutoMirrored.Rounded.ArrowBack, true, onClick, subtle = true)

private data class HomeAction(val label: String, val icon: ImageVector, val onClick: () -> Unit)

internal fun homeActionLabels(
    user: UserProfile?,
    capabilities: Set<String> = Protocol.BASELINE_CAPABILITIES,
): List<String> = buildList {
    if (user != null && Protocol.CAP_SCHEDULE_READ in capabilities) add("课表")
    if (user?.has(Protocol.PERMISSION_MANAGE_SCHEDULE) == true && Protocol.CAP_SCHEDULE_CHANGE in capabilities) add("换课")
    if (user?.has(Protocol.PERMISSION_TEACHER_COMING) == true && Protocol.CAP_TEACHER_COMING in capabilities ||
        user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true &&
            (Protocol.CAP_NOTIFICATION_SEND in capabilities || Protocol.CAP_NOTIFICATION_CLEAR in capabilities) ||
        user?.has(Protocol.PERMISSION_POWER_CONTROL) == true &&
            (Protocol.CAP_POWER_CONTROL in capabilities || Protocol.CAP_VOLUME_CONTROL in capabilities) ||
        user?.has(Protocol.PERMISSION_MAIN_MENU_CONTROL) == true && Protocol.CAP_MAIN_MENU_VISIBILITY in capabilities ||
        user?.has(Protocol.PERMISSION_RUN_EXTENSIONS) == true && Protocol.CAP_EXTENSIONS_RUN in capabilities ||
        user?.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) == true && Protocol.CAP_VOICE_MESSAGE_SEND in capabilities) add("控制")
    add("设置")
}

internal fun canViewExtendedSchedule(user: UserProfile?): Boolean =
    user != null

@Composable
private fun HomeInfoChip(
    text: String,
    icon: ImageVector? = null,
    filled: Boolean,
    modifier: Modifier = Modifier.height(24.dp),
    fontSize: TextUnit = if (filled) 11.sp else 12.sp,
    iconSize: Dp = 16.dp,
    onClick: (() -> Unit)? = null,
) {
    Row(
        modifier = modifier.clip(RoundedCornerShape(12.dp))
            .then(
                if (filled) Modifier.background(palette.buttonContainer)
                else Modifier.border(1.dp, Color(0xFF79747E), RoundedCornerShape(12.dp)),
            )
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 7.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(icon, null, tint = palette.onButtonContainer, modifier = Modifier.size(iconSize))
            Spacer(Modifier.width(5.dp))
        }
        Text(
            text,
            color = if (filled) palette.onButtonContainer else Color.Black,
            fontSize = fontSize,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun LessonSummary(course: CourseEntry) {
    Row(
        modifier = Modifier.fillMaxWidth(.82f).height(38.dp).clip(RoundedCornerShape(14.dp))
            .background(palette.buttonContainer).padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(course.label, color = palette.onButtonContainer, fontSize = 11.sp)
        Spacer(Modifier.width(7.dp))
        Text(course.subject, color = palette.onButtonContainer, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f), maxLines = 1)
        Text(listOfNotNull(course.startTime, course.endTime).joinToString("-"), color = palette.onButtonContainer, fontSize = 9.sp)
    }
}

@Composable
private fun LessonButton(label: String, subject: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth(.78f).height(44.dp).clip(RoundedCornerShape(12.dp)).clickable(onClick = onClick)) {
        Box(Modifier.weight(.3f).fillMaxSize().background(palette.secondaryContainer), contentAlignment = Alignment.Center) { Text(label, color = Color.Black, fontWeight = FontWeight.Bold) }
        Box(Modifier.weight(.7f).fillMaxSize().background(palette.primaryContainer), contentAlignment = Alignment.Center) { Text(subject, color = Color.Black, fontWeight = FontWeight.Bold, maxLines = 1) }
    }
}

@Composable
private fun ModeSelector(mode: SwapMode, onModeChange: (SwapMode) -> Unit) {
    Row(Modifier.width(120.dp).height(28.dp).clip(RoundedCornerShape(14.dp)).background(palette.disabledContainer)) {
        listOf(SwapMode.Exchange to stringResource(R.string.swap_mode_exchange), SwapMode.Replace to stringResource(R.string.swap_mode_replace)).forEach { (value, label) ->
            Box(
                Modifier.weight(1f).fillMaxSize().background(if (mode == value) palette.buttonContainer else Color.Transparent).clickable { onModeChange(value) },
                contentAlignment = Alignment.Center,
            ) { Text(label, color = if (mode == value) palette.onButtonContainer else Color.White, fontSize = 11.sp) }
        }
    }
}

@Composable
private fun Input(value: String, onValueChange: (String) -> Unit, label: String, password: Boolean = false) {
    TextField(
        value = value,
        onValueChange = onValueChange,
        modifier = Modifier.fillMaxWidth(.86f),
        label = { androidx.compose.material3.Text(label) },
        singleLine = true,
        visualTransformation = if (password) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        colors = TextFieldDefaults.colors(
            focusedTextColor = Color.White, unfocusedTextColor = Color.White,
            focusedContainerColor = palette.disabledContainer, unfocusedContainerColor = palette.disabledContainer,
            focusedIndicatorColor = palette.buttonContainer, cursorColor = palette.buttonContainer,
        ),
    )
}

@Composable
private fun Toggle(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    ToggleChip(
        checked = checked,
        onCheckedChange = onChange,
        label = { Text(label) },
        toggleControl = { Switch(checked = checked) },
        colors = ToggleChipDefaults.toggleChipColors(uncheckedStartBackgroundColor = palette.disabledContainer, uncheckedEndBackgroundColor = palette.disabledContainer),
        modifier = Modifier.fillMaxWidth(.88f),
    )
}

@Composable private fun Hint(text: String, onClick: (() -> Unit)? = null) = Text(
    text,
    color = Color.White.copy(.68f),
    fontSize = 10.sp,
    textAlign = TextAlign.Center,
    modifier = Modifier.fillMaxWidth(.76f).then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
)

@Composable
private fun describeConnectionForScreen(state: ConnectionManager.State): String = when (state) {
    ConnectionManager.State.Idle -> stringResource(R.string.connection_state_idle)
    ConnectionManager.State.Connecting -> stringResource(R.string.connection_state_connecting)
    ConnectionManager.State.LanConnected -> stringResource(R.string.connection_state_lan)
    ConnectionManager.State.CloudConnected -> stringResource(R.string.connection_state_cloud)
    is ConnectionManager.State.Error -> state.message
}

/** 放学后以明天作为浏览和换课下限，防止继续展示已经结束的当天课表。 */
internal fun availableScheduleDays(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): List<ScheduleDay> {
    val firstVisibleDate = if (afterSchool) today.plusDays(1) else today
    return bundle?.days.orEmpty()
        .filter { day -> parseScheduleDate(day.date)?.isBefore(firstVisibleDate) != true }
        .sortedBy { day -> parseScheduleDate(day.date) ?: LocalDate.MAX }
}

internal fun initialScheduleDate(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): String? = availableScheduleDays(bundle, afterSchool, today).firstOrNull()?.date

internal fun scheduleDateTitle(date: String, today: LocalDate = LocalDate.now()): String {
    val suffix = when (parseScheduleDate(date)) {
        today -> "-今天"
        today.plusDays(1) -> "-明天"
        else -> ""
    }
    return "${date.takeLast(5)}$suffix"
}

private fun parseScheduleDate(date: String): LocalDate? = runCatching { LocalDate.parse(date) }.getOrNull()

internal fun buildLessonChoices(day: ScheduleDay?): List<LessonChoice> = day?.courses?.map { it.toChoice() }.orEmpty()

/** 保留当前课程映射测试，产品换课路径使用上面的 ScheduleDay 重载。 */
internal fun buildLessonChoices(snapshot: ClassStateSnapshot?): List<LessonChoice> = listOf(
    LessonChoice("current", 0, extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", snapshot?.currentSubject ?: "未加载课表", extractTimeRange(snapshot?.currentTimeLayoutItem), extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", !snapshot?.currentSubject.isNullOrBlank()),
    LessonChoice("next", 1, extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", snapshot?.nextClassSubject ?: "暂无下一节", extractTimeRange(snapshot?.nextClassTimeLayoutItem), extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", !snapshot?.nextClassSubject.isNullOrBlank()),
)

private fun CourseEntry.toChoice() = LessonChoice(
    id = index.toString(), index = index, periodLabel = label, subject = subject,
    time = listOfNotNull(startTime, endTime).joinToString("-"), commandValue = index.toString(), enabled = enabled,
)

private val TimeRangeRegex = Regex("(\\d{1,2}:\\d{2})\\s*[-–—~至]\\s*(\\d{1,2}:\\d{2})")
private val LessonPeriodRegex = Regex("第[一二三四五六七八九十\\d]+节")
private fun extractPeriod(value: String?): String? = value?.let(LessonPeriodRegex::find)?.value
internal fun extractTimeRange(value: String?): String {
    val match = value?.let(TimeRangeRegex::find) ?: return ""
    return "${match.groupValues[1]}-${match.groupValues[2]}"
}

/**
 * 把当前时间段（如 "16:30-17:10 语文"）匹配到当天课表的课程零基索引，
 * 供主界面课程按钮“点击快速选中该课换课”预选源课使用。
 * 匹配不到（课表未同步、当前无课或时间段不一致）时返回 null。
 */
internal fun currentLessonIndex(day: ScheduleDay?, value: String?): Int? {
    val range = extractTimeRange(value)
    if (range.isBlank() || day == null) return null
    val parts = range.split("-")
    if (parts.size != 2) return null
    val (start, end) = parts
    return day.courses.firstOrNull { it.enabled && it.startTime == start && it.endTime == end }?.index
}

internal data class HomeCourseContent(
    val subject: String,
    val timeLayoutItem: String?,
    val targetsNextLesson: Boolean,
    val isAvailable: Boolean,
)

/**
 * 决定主页主课程按钮代表当前课还是下一节课。
 * 下课/即将上课阶段没有正在进行的课程，因此按钮直接承载下一节课及其快速换课入口。
 */
internal fun homeCourseContent(snapshot: ClassStateSnapshot?): HomeCourseContent {
    val targetsNextLesson = snapshot?.currentState in setOf(
        Protocol.STATE_BREAKING,
        Protocol.STATE_PREPARE_CLASS,
    )
    val subject = if (targetsNextLesson) snapshot?.nextClassSubject else snapshot?.currentSubject
    val availableSubject = subject?.trim()?.takeIf(String::isNotEmpty)
    return if (targetsNextLesson) {
        HomeCourseContent(
            subject = availableSubject ?: "暂无下一节",
            timeLayoutItem = snapshot?.nextClassTimeLayoutItem,
            targetsNextLesson = true,
            isAvailable = availableSubject != null,
        )
    } else {
        HomeCourseContent(
            subject = availableSubject ?: "暂无课程",
            timeLayoutItem = snapshot?.currentTimeLayoutItem,
            targetsNextLesson = false,
            isAvailable = availableSubject != null,
        )
    }
}

/** 主按钮已经显示下一节课时，不再在页面底部重复显示“下一节课是”。 */
internal fun shouldShowNextLessonSummary(currentState: Int?): Boolean =
    currentState !in setOf(Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS)

/** 快速换课预选项必须与主页按钮当前显示的课程一致。 */
internal fun homeQuickSwapLessonIndex(day: ScheduleDay?, snapshot: ClassStateSnapshot?): Int? =
    currentLessonIndex(day, homeCourseContent(snapshot).timeLayoutItem)

/** “暂无课程”状态下的下一节课入口始终按下一时间段匹配，不能回退到空的当前时间段。 */
internal fun nextQuickSwapLessonIndex(day: ScheduleDay?, snapshot: ClassStateSnapshot?): Int? =
    currentLessonIndex(day, snapshot?.nextClassTimeLayoutItem)

/** 仅在截图所示的“当前无课、但有下一节课”状态展示下一节课描边操作入口。 */
internal fun shouldHighlightNextLessonAction(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.currentState == Protocol.STATE_NONE &&
        !homeCourseContent(snapshot).isAvailable &&
        !snapshot.nextClassSubject.isNullOrBlank()

/** 只有具有明确起止时间的课程阶段才显示环状进度，避免放学等开放状态产生伪进度。 */
internal fun shouldShowStateProgress(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.currentState in setOf(
        Protocol.STATE_CLASS,
        Protocol.STATE_PREPARE_CLASS,
        Protocol.STATE_BREAKING,
    ) && extractTimeRange(snapshot?.currentTimeLayoutItem).isNotBlank()

internal fun lessonProgress(value: String?, now: LocalTime): Float {
    val match = value?.let(TimeRangeRegex::find) ?: return 0f
    val start = runCatching { LocalTime.parse(match.groupValues[1]) }.getOrNull() ?: return 0f
    val end = runCatching { LocalTime.parse(match.groupValues[2]) }.getOrNull() ?: return 0f
    val total = Duration.between(start, end).seconds
    return if (total <= 0) 0f else (Duration.between(start, now).seconds.toFloat() / total).coerceIn(0f, 1f)
}

/**
 * 根据插件快照推算“插件本地当前时间”：
 * 以快照的 UTC 生成时间为基准，加上插件时区偏移和本机经过的真实时间，
 * 即使手表时区与插件不一致，课程进度也能按插件时间轴正确计算。
 *
 * @param generatedAt 快照的 UTC 生成时间（ISO-8601，如 "2026-08-12T08:46:55+00:00"）。
 * @param offsetMinutes 插件本地时区相对 UTC 的偏移分钟数；为 null 表示旧版插件无偏移信息。
 * @param baseElapsedMs 收到当前快照时本机 elapsedRealtime 毫秒数。
 * @param nowElapsedMs 当前本机 elapsedRealtime 毫秒数。
 */
internal fun pluginLocalNow(
    generatedAt: String?,
    offsetMinutes: Int?,
    baseElapsedMs: Long,
    nowElapsedMs: Long,
): LocalTime {
    val offset = offsetMinutes?.let { runCatching { ZoneOffset.ofTotalSeconds(it * 60) }.getOrNull() }
    if (offset == null) return LocalTime.now()
    val base = generatedAt?.let { raw ->
        runCatching { OffsetDateTime.parse(raw).toInstant().atOffset(offset).toLocalTime() }.getOrNull()
    } ?: LocalTime.now(offset)
    return base.plusNanos((nowElapsedMs - baseElapsedMs) * 1_000_000L)
}

/**
 * 推算插件侧“今天”的日期，与 [pluginLocalNow] 同源：以快照 UTC 生成时间加插件时区偏移为基准，
 * 再按本机流逝时间外推。手表与插件时区不一致时不会取错日期；缺时区信息退回表端日期。
 */
internal fun pluginToday(
    generatedAt: String?,
    offsetMinutes: Int?,
    baseElapsedMs: Long,
    nowElapsedMs: Long,
): LocalDate {
    val offset = offsetMinutes?.let { runCatching { ZoneOffset.ofTotalSeconds(it * 60) }.getOrNull() }
    if (offset == null) return LocalDate.now()
    val base = generatedAt?.let { raw ->
        runCatching { OffsetDateTime.parse(raw).toInstant().atOffset(offset).toLocalDateTime() }.getOrNull()
    } ?: LocalDateTime.now(offset)
    return base.plusNanos((nowElapsedMs - baseElapsedMs) * 1_000_000L).toLocalDate()
}
