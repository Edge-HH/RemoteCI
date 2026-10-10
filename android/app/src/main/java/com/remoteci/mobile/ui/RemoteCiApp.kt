package com.remoteci.mobile.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.EventHistory
import com.remoteci.mobile.data.SettingsStore
import com.remoteci.mobile.data.widgetUsesPersonalSchedule
import com.remoteci.mobile.data.WatchSettings
import com.remoteci.mobile.notif.NotificationHelper
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

/** SwapRequests：老师换课申请页，仅在账号拥有“老师主动换课”权限时出现在底栏。 */
enum class HomeTab { Today, Schedule, SwapRequests, Control, People }

sealed interface Screen {
    data object Login : Screen
    data class Home(val tab: HomeTab = HomeTab.Today) : Screen
    data object ClassPicker : Screen
    data class Swap(val date: String?, val index: Int?) : Screen
    data object Account : Screen
    data object Inbox : Screen
    data object Notify : Screen
    data object Voice : Screen
    data object Volume : Screen
    data object Power : Screen
    data object MainMenu : Screen
    data object Extensions : Screen
    data object VisitorAccess : Screen
    data object Roles : Screen
    data object Users : Screen
    data object Sessions : Screen
    data object Pairing : Screen
    data object Connection : Screen
    data object NotificationSettings : Screen
    data object ScheduleSettings : Screen
    data object Appearance : Screen
    data object Updates : Screen
    data object Developer : Screen
    data object System : Screen
    data object Help : Screen
    data object Replace : Screen
}

@Composable
fun RemoteCiApp(appContext: android.content.Context) {
    val store = remember { SettingsStore(appContext) }
    var settings by remember { mutableStateOf(store.load()) }
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val connection by ConnectionManager.state.collectAsState()
    var stack by remember { mutableStateOf(listOf<Screen>(if (ConnectionManager.hasSavedSession()) Screen.Home() else Screen.Login)) }
    val current = stack.last()
    val history = remember { EventHistory(appContext) }
    var navigationDirection by remember { mutableIntStateOf(1) }

    DisposableEffect(Unit) {
        ConnectionManager.initialize(appContext)
        onDispose { }
    }

    // 首次启动与回到前台都交给 onForeground：已连接时不再断开重连（扫码、选文件等短暂离开也不打断连接），
    // 只有未连接、失败或在后台停留过久时才重建。观察者注册时会立即收到 ON_START，覆盖冷启动。
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> ConnectionManager.onForeground(settings)
                Lifecycle.Event.ON_STOP -> ConnectionManager.onBackground()
                else -> Unit
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }

    LaunchedEffect(Unit) {
        ConnectionManager.events.collectLatest { event ->
            NotificationHelper.handle(appContext, event, settings, history)
        }
    }
    LaunchedEffect(Unit) {
        ConnectionManager.userNotifications.collect { notification ->
            NotificationHelper.handleUser(appContext, notification, history)
        }
    }
    LaunchedEffect(Unit) {
        com.remoteci.mobile.MainActivity.openRequests.collect { target ->
            if (target == NotificationHelper.OPEN_SWAP_REQUESTS && stack.last() !is Screen.Login) {
                navigationDirection = 1
                stack = listOf(Screen.Home(HomeTab.SwapRequests))
            }
            if (target == NotificationHelper.OPEN_SCHEDULE && stack.last() !is Screen.Login) {
                navigationDirection = 1
                stack = listOf(Screen.Home(HomeTab.Schedule))
            }
            if (target != null) com.remoteci.mobile.MainActivity.openRequests.value = null
        }
    }
    LaunchedEffect(Unit) {
        ConnectionManager.lastCommandResult.collectLatest { result ->
            result ?: return@collectLatest
            snackbar.showSnackbar(if (result.success) "已完成：${result.message.ifBlank { "成功" }}" else "失败：${result.message.ifBlank { result.code }}")
        }
    }
    LaunchedEffect(Unit) {
        combine(
            ConnectionManager.snapshot,
            ConnectionManager.personalNextCourse,
            ConnectionManager.currentUser,
        ) { snapshot, personal, profile -> Triple(snapshot, personal, widgetUsesPersonalSchedule(profile)) }
            .collectLatest { (snapshot, personal, isTeacher) ->
                NotificationHelper.updateSchoolStatus(appContext, snapshot, personal, isTeacher)
            }
    }
    LaunchedEffect(Unit) {
        ConnectionManager.discoveredSettings.collectLatest {
            settings = it
            store.save(it)
        }
    }

    fun persist(next: WatchSettings) {
        settings = next
        store.save(next)
    }

    fun push(screen: Screen) {
        navigationDirection = 1
        stack = stack + screen
    }
    fun pop() {
        if (stack.size > 1) {
            navigationDirection = -1
            stack = stack.dropLast(1)
        }
    }
    fun goHome(tab: HomeTab) {
        navigationDirection = -1
        stack = listOf(Screen.Home(tab))
    }

    val appearance = when (settings.appearanceMode) {
        "light" -> AppearanceMode.Light
        "dark" -> AppearanceMode.Dark
        else -> AppearanceMode.System
    }

    RemoteCiTheme(
        appearanceMode = appearance,
        palette = MobilePalette.fromId(settings.themeId),
        dynamicColor = settings.themeId == DynamicThemeId,
    ) {
        Box(Modifier.fillMaxSize().imePadding()) {
            AnimatedContent(
                targetState = current,
                modifier = Modifier.fillMaxSize(),
                // 四个首页板块共用同一个 HomeShell；切换底栏只替换正文，不让底栏跟着横向滑动。
                contentKey = ::screenTransitionKey,
                transitionSpec = {
                    if (navigationDirection >= 0) {
                        (fadeIn(tween(180)) + slideInHorizontally(tween(220)) { it / 12 }) togetherWith
                            (fadeOut(tween(140)) + slideOutHorizontally(tween(180)) { -it / 16 })
                    } else {
                        (fadeIn(tween(180)) + slideInHorizontally(tween(220)) { -it / 12 }) togetherWith
                            (fadeOut(tween(140)) + slideOutHorizontally(tween(180)) { it / 16 })
                    }
                },
                label = "screen-transition",
            ) { screen ->
                when (screen) {
                    Screen.Login -> LoginScreen(
                        settings = settings,
                        connection = connection,
                        snackbar = snackbar,
                        onSettings = { persist(it) },
                        onLoggedIn = {
                            // 多班级账号登录后先选择进入的班级，单班级账号直接进首页。
                            if (ConnectionManager.classes.value.size > 1) push(Screen.ClassPicker)
                            else goHome(HomeTab.Today)
                        },
                    )
                    Screen.ClassPicker -> ClassPickerScreen(
                        onPicked = { goHome(HomeTab.Today) },
                    )
                    is Screen.Home -> HomeShell(
                        tab = screen.tab,
                        settings = settings,
                        snackbar = snackbar,
                        onTab = { tab ->
                            val currentTab = (current as? Screen.Home)?.tab
                            navigationDirection = if (currentTab != null &&
                                HomeTab.entries.indexOf(tab) < HomeTab.entries.indexOf(currentTab)) -1 else 1
                            stack = listOf(Screen.Home(tab))
                        },
                        onOpen = ::push,
                        onPersist = ::persist,
                    )
                    else -> SecondaryHost(
                        screen = screen,
                        settings = settings,
                        snackbar = snackbar,
                        onBack = ::pop,
                        onOpen = ::push,
                        onHome = ::goHome,
                        onPersist = ::persist,
                        onLoggedOut = { stack = listOf(Screen.Login) },
                    )
                }
            }
            // 启动时自动检查 App 更新，发现新版本时弹窗（登录页同样适用）。
            AutoUpdatePrompt(settings = settings, onPersist = ::persist)
            SnackbarHost(
                hostState = snackbar,
                modifier = Modifier.align(Alignment.BottomCenter)
                    .padding(horizontal = 16.dp, vertical = if (current is Screen.Home) 84.dp else 16.dp),
            )
        }
    }
}

/** 首页底栏属于固定应用框架，四个板块必须共享动画 key，仅二级页面导航参与整页转场。 */
internal fun screenTransitionKey(screen: Screen): Any = if (screen is Screen.Home) "home" else screen
