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
import com.remoteci.mobile.data.WatchSettings
import com.remoteci.mobile.notif.NotificationHelper
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

enum class HomeTab { Today, Schedule, Control, People }

sealed interface Screen {
    data object Login : Screen
    data class Home(val tab: HomeTab = HomeTab.Today) : Screen
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
    val user by ConnectionManager.currentUser.collectAsState()
    var stack by remember { mutableStateOf(listOf<Screen>(if (ConnectionManager.hasSavedSession()) Screen.Home() else Screen.Login)) }
    val current = stack.last()
    val history = remember { EventHistory(appContext) }

    DisposableEffect(Unit) {
        ConnectionManager.initialize(appContext)
        if (ConnectionManager.hasSavedSession()) ConnectionManager.connect(settings)
        onDispose { }
    }

    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_START && ConnectionManager.hasSavedSession()) {
                ConnectionManager.connect(settings)
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
        ConnectionManager.lastCommandResult.collectLatest { result ->
            result ?: return@collectLatest
            snackbar.showSnackbar(if (result.success) "已完成：${result.message.ifBlank { "成功" }}" else "失败：${result.message.ifBlank { result.code }}")
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

    fun push(screen: Screen) { stack = stack + screen }
    fun pop() { if (stack.size > 1) stack = stack.dropLast(1) }
    fun goHome(tab: HomeTab) { stack = listOf(Screen.Home(tab)) }

    val appearance = when (settings.appearanceMode) {
        "light" -> AppearanceMode.Light
        "dark" -> AppearanceMode.Dark
        else -> AppearanceMode.System
    }

    RemoteCiTheme(appearance, MobilePalette.fromId(settings.themeId)) {
        Box(Modifier.fillMaxSize().imePadding()) {
            AnimatedContent(
                targetState = current,
                modifier = Modifier.fillMaxSize(),
                // 四个首页板块共用同一个 HomeShell；切换底栏只替换正文，不让底栏跟着横向滑动。
                contentKey = ::screenTransitionKey,
                transitionSpec = {
                    (fadeIn(tween(180)) + slideInHorizontally(tween(220)) { it / 12 }) togetherWith
                        (fadeOut(tween(140)) + slideOutHorizontally(tween(180)) { -it / 16 })
                },
                label = "screen-transition",
            ) { screen ->
                when (screen) {
                    Screen.Login -> LoginScreen(
                        settings = settings,
                        connection = connection,
                        snackbar = snackbar,
                        onSettings = { persist(it) },
                        onLoggedIn = { goHome(HomeTab.Today) },
                    )
                    is Screen.Home -> HomeShell(
                        tab = screen.tab,
                        settings = settings,
                        snackbar = snackbar,
                        onTab = { tab -> stack = listOf(Screen.Home(tab)) },
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
