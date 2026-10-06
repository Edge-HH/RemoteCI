package com.remoteci.mobile.data

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.SystemClock
import com.remoteci.mobile.BuildConfig
import com.remoteci.mobile.widget.WidgetUpdater
import java.io.IOException
import java.security.MessageDigest
import java.time.OffsetDateTime
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.coroutines.resume
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.KSerializer
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.JsonElement
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/** 云端和局域网共用的认证、状态与命令入口。 */
object ConnectionManager {
    sealed interface State {
        data object Idle : State
        data object Connecting : State
        data object LanConnected : State
        data object CloudConnected : State
        data class Error(val message: String) : State
    }

    sealed interface SchedulePullState {
        data object Idle : SchedulePullState
        data class Pulling(val message: String) : SchedulePullState
        data class Success(val message: String) : SchedulePullState
        data class Error(val message: String) : SchedulePullState
    }

    /** 一次 WebSocket 认证尝试的结果；升级请求被拒（401）说明令牌已失效，调用方需换新令牌重试。 */
    private enum class SocketOutcome { Authenticated, Failed, Unauthorized }

    /** 已签发的访问令牌；到期时刻按服务器时钟换算到本机，避免手机时间偏差导致续期过晚。 */
    private class IssuedAuth(val response: AuthResponse, val serverUrl: String, val expiresAtMs: Long) {
        fun usableFor(serverUrl: String, deviceSessionId: String, nowMs: Long): Boolean =
            this.serverUrl == serverUrl && response.deviceSessionId == deviceSessionId &&
                expiresAtMs - nowMs > TokenReuseMarginMs
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = protocolJson
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        // 静默断网（Wi-Fi 掉线、NAT 失效）时 TCP 不会通知应用，依赖 WebSocket ping 主动探测，
        // 否则连接会“看起来还活着”却永远收不到数据、永不重连。
        .pingInterval(20, TimeUnit.SECONDS)
        .build()
    // 登录、刷新等短请求必须限时：readTimeout=0 时服务端或代理卡住会让界面永远停在“正在连接”，
    // 刷新期间还持有会话锁，会连带阻塞之后的所有重连。
    private val authHttp = okHttp.newBuilder()
        .readTimeout(10, TimeUnit.SECONDS)
        .callTimeout(20, TimeUnit.SECONDS)
        .build()
    // 无效的局域网候选只等待很短时间；云端请求仍使用上面的 6 秒超时。
    private val lanOkHttp = okHttp.newBuilder()
        .connectTimeout(1_500, TimeUnit.MILLISECONDS)
        .build()
    private val lanDiscoveryClient = LanDiscoveryClient(lanOkHttp, json)
    private lateinit var sessions: SessionStorage
    private var appContext: Context? = null
    /** 最近一次有效推送同时供桌面小组件离线展示。 */
    private var snapshotStore: SnapshotStore? = null
    @Volatile private var lastWidgetSnapshotPersistAt = 0L
    // 以下字段会被 OkHttp 回调线程、IO 协程与主线程并发读写，必须保证跨线程可见性。
    @Volatile private var webSocket: WebSocket? = null
    /** 活跃连接的切换（并发探测、令牌轮换、断线清理）必须原子完成。 */
    private val socketLock = Any()
    private var activeJob: Job? = null
    private var refreshJob: Job? = null
    private var reconnectJob: Job? = null
    private var volumeJob: Job? = null
    private var discoveryJob: Job? = null
    private var schedulePullJob: Job? = null
    private var personalScheduleJob: Job? = null
    @Volatile private var personalScheduleUserId: String? = null
    private var swapInboxJob: Job? = null
    @Volatile private var swapInboxUserId: String? = null
    @Volatile private var desiredSettings: WatchSettings? = null
    /** 最近一次能力快照：切换班级时按新班级重新计算可用控制项，无需等待服务端重发。 */
    @Volatile private var lastCapabilitiesSync: CapabilitiesSync? = null
    // 刷新会同时轮换访问令牌与设备密钥，旧值立即失效：并发刷新会让后到的一方 401，
    // 刷新结果没落盘则下一次刷新 401——两种情况都会把用户踢回登录页。所有签发都在此锁内串行完成。
    private val sessionMutex = Mutex()
    @Volatile private var issuedAuth: IssuedAuth? = null
    /** 退出登录后递增：在途刷新完成时不得再把已退出的会话写回存储。 */
    private val sessionEpoch = AtomicInteger(0)
    @Volatile private var lastLanAdvertisement: PluginNetworkInfo? = null
    /** 服务端按班级下发的插件局域网地址；换班时据此切换直连候选。 */
    private val pluginNetworkInfos = ConcurrentHashMap<String, PluginNetworkInfo>()
    // 多班级账号会持续收到每个可访问班级的推送：按班级缓存，换班立即展示，不再等下一次推送。
    private val classSnapshots = ConcurrentHashMap<String, ClassStateSnapshot>()
    private val classSchedules = ConcurrentHashMap<String, ScheduleBundle>()
    private val classExtensions = ConcurrentHashMap<String, List<ExtensionDefinition>>()
    // OkHttp 回调线程、主线程与协程会并发读写 generation，必须用原子操作保证 attempt 校验可靠。
    private val generation = AtomicInteger(0)
    // 断线自动重连的指数退避（连接成功后复位）；避免服务端抖动时 5 秒固定间隔造成重连风暴。
    @Volatile private var reconnectDelayMs = InitialReconnectDelayMs
    /** 云端明确拒绝了本机协议版本：自动重试没有意义，等待用户升级。 */
    @Volatile private var protocolRejected = false
    /** 最近一次局域网握手被插件拒绝的原因，仅局域网模式失败时用于提示。 */
    @Volatile private var lastHandshakeError: String? = null
    @Volatile private var backgroundSinceMs = 0L
    @Volatile private var defaultNetwork: Network? = null

    val state = MutableStateFlow<State>(State.Idle)
    /** 当前认证连接所属的 WebUI 版本；未知时禁止手表自行升级。 */
    val serverVersion = MutableStateFlow<String?>(null)
    /** 本机、服务端与当前主插件共同支持的功能；旧 V3 端缺少声明时按基础能力回退。 */
    val availableCapabilities = MutableStateFlow(Protocol.BASELINE_CAPABILITIES)
    val currentUser = MutableStateFlow<UserProfile?>(null)
    /** 首登设置密码挑战：批量导入且未设密码的账号空密码登录时下发，界面据此展示设置表单。 */
    val pendingPasswordSetup = MutableStateFlow<PasswordSetupChallenge?>(null)
    /** 账号可访问的班级与当前选中班级；单班级部署列表只有一个元素，界面据此隐藏切换器。 */
    val classes = MutableStateFlow<List<ClassSummary>>(emptyList())
    val currentClassId = MutableStateFlow<String?>(null)
    val snapshot = MutableStateFlow<ClassStateSnapshot?>(null)
    val schedule = MutableStateFlow<ScheduleBundle?>(null)
    /** 老师跨班级个人日程的当前/下一节课，供首页和系统状态通知共用。 */
    val personalNextCourse = MutableStateFlow<MyNextCourseResponse?>(null)
    val extensions = MutableStateFlow<List<ExtensionDefinition>>(emptyList())
    val settings = MutableStateFlow<SettingsSync?>(null)
    val events = MutableSharedFlow<ClassEvent>(extraBufferCapacity = 32)
    /** 个人通知（换课申请等）：实时 user_notify 与定时补齐共用，界面层按 Id 去重后发系统通知。 */
    val userNotifications = MutableSharedFlow<UserNotification>(extraBufferCapacity = 32)
    /** 待我处理的换课申请数（待审批或可撤回的强制换课），用于底栏角标。 */
    val pendingSwapCount = MutableStateFlow(0)
    val lastCommandResult = MutableStateFlow<CommandResult?>(null)
    private val voiceReplies = java.util.concurrent.ConcurrentHashMap<String, kotlinx.coroutines.CompletableDeferred<CommandResult>>()

    /** 语音体积较大，在后台编码，并只接受本条消息的回执；断线时不自动重发。 */
    suspend fun sendVoiceMessage(audio: ByteArray): CommandResult = withContext(Dispatchers.IO) {
        if (!hasClassPermission(Protocol.PERMISSION_SEND_VOICE_MESSAGES))
            return@withContext CommandResult(false, "FORBIDDEN", "没有发送语音权限")
        if (!supports(Protocol.CAP_VOICE_MESSAGE_SEND))
            return@withContext CommandResult(false, "CAPABILITY_UNSUPPORTED", "请更新服务端和插件以支持语音")
        if (audio.isEmpty() || audio.size > 16000 * 2 * 60 || audio.size % 2 != 0)
            return@withContext CommandResult(false, "INVALID_REQUEST", "录音无效或超过 60 秒")
        val id = newMessageId()
        val reply = kotlinx.coroutines.CompletableDeferred<CommandResult>()
        voiceReplies[id] = reply
        try {
            val command = CommandMessage(command = Protocol.CMD_SEND_VOICE_MESSAGE,
                classId = currentClassId.value,
                voiceMessage = VoiceMessageRequest(audioBase64 = android.util.Base64.encodeToString(audio, android.util.Base64.NO_WRAP)))
            val envelope = Envelope(type = Protocol.TYPE_COMMAND, messageId = id,
                payload = json.encodeToJsonElement(CommandMessage.serializer(), command))
            if (webSocket?.send(encodeEnvelope(envelope)) != true)
                return@withContext CommandResult(false, "OFFLINE", "连接已断开，语音未发送")
            withTimeout(20_000) { reply.await() }
        } catch (_: TimeoutCancellationException) {
            CommandResult(false, "COMMAND_TIMEOUT", "未收到回执，请确认课表端是否已播放后再重试")
        } finally { voiceReplies.remove(id) }
    }
    val schedulePullState = MutableStateFlow<SchedulePullState>(SchedulePullState.Idle)
    /** 网络发现、成功直连或切换班级后产生的本地设置更新，由界面层持久化。 */
    val discoveredSettings = MutableSharedFlow<WatchSettings>(extraBufferCapacity = 1)
    val lanPlugins = MutableStateFlow<List<LanPluginCandidate>>(emptyList())
    val lanDiscoveryStatus = MutableStateFlow<String?>(null)
    val lanDiscoveryScanning = MutableStateFlow(false)
    /** 引导返回的云服务器地址与上次使用的不同：等待用户二次确认的候选（TOFU 强阻断）。 */
    val lanBootstrapPending = MutableStateFlow<Pair<LanPluginCandidate, WatchSettings>?>(null)

    fun initialize(context: Context) {
        val app = context.applicationContext
        if (appContext == null) {
            appContext = app
            SnapshotStore.deleteLegacy(app)
            snapshotStore = SnapshotStore(app)
            registerNetworkCallback(app)
        }
        if (!::sessions.isInitialized) sessions = SecureSessionStore(app)
    }

    private fun localNetworkAllowed(): Boolean = appContext?.let(::hasLocalNetworkPermission) ?: true

    /** 仅供 JVM 单元测试注入内存会话存储，绕开 Android Keystore。 */
    internal fun installSessionStorageForTest(storage: SessionStorage) {
        sessions = storage
    }

    fun hasSavedSession(): Boolean = ::sessions.isInitialized && sessions.load() != null
    fun restBaseUrl(): String? = desiredSettings?.cloudServerUrl?.let(::normalizeServerUrl)
    fun restToken(): String? = issuedAuth?.takeIf { it.serverUrl == restBaseUrl() }?.response?.accessToken

    /** 从服务端拉取最新档案（/api/me）：修改显示名（老师姓名）后刷新本地班级与权限。 */
    suspend fun refreshProfile() {
        applyUserProfile(AdminApi.me())
    }

    fun supports(capability: String): Boolean = capability in availableCapabilities.value

    /** 统一写入用户档案：同时刷新班级列表；已选班级失效时回退到第一个可访问班级。 */
    private fun applyUserProfile(user: UserProfile?) {
        currentUser.value = user
        syncPersonalSchedule(user)
        syncSwapInbox(user)
        val previous = currentClassId.value
        if (user == null) {
            classes.value = emptyList()
            currentClassId.value = null
            bindWidgetOwner(null, null)
            return
        }
        // 插件局域网镜像中的账号不带班级列表：沿用云端最近一次下发的班级与当前选择，
        // 否则直连后班级列表被清空、已选班级被重置，换班入口与按班级的权限都会失效。
        val accessible = user.classes ?: run {
            bindWidgetOwner(user, previous)
            return
        }
        classes.value = accessible
        val selected = previous ?: desiredSettings?.selectedClassId?.takeIf(String::isNotBlank)
        val next = if (selected != null && accessible.any { it.id == selected }) selected
        else accessible.firstOrNull()?.id
        currentClassId.value = next
        bindWidgetOwner(user, next)
        // 冷启动走局域网时，插件推送早于班级确定、未能按班级缓存：先归入刚确定的班级，
        // 否则下面的重新发布会把已展示的课表清空，直到插件下次推送课表才恢复。
        if (previous == null && next != null) adoptUnattributedData(next)
        if (previous != next) publishClassData(next)
    }

    /**
     * 老师与班主任的个人日程不随某个当前班级推送，单独按短周期刷新以支持多班级授课。
     * 只有老师的小组件与常驻通知以个人日程为主；班主任默认仍展示本班状态，个人日程仅供首页“我的”视图。
     */
    private fun syncPersonalSchedule(user: UserProfile?) {
        val userId = personalScheduleUserId(user)
        if (userId == personalScheduleUserId && personalScheduleJob?.isActive == true) return
        personalScheduleJob?.cancel()
        personalScheduleUserId = userId
        personalNextCourse.value = null
        snapshotStore?.clearPersonalNext()
        appContext?.let { WidgetUpdater.updateAll(it, force = true) }
        if (userId == null) return
        val persistForWidget = widgetUsesPersonalSchedule(user)
        personalScheduleJob = scope.launch {
            var lastSuccessAt = 0L
            while (kotlin.coroutines.coroutineContext.isActive) {
                runCatching { AdminApi.myNextCourse() }
                    .onSuccess {
                        lastSuccessAt = SystemClock.elapsedRealtime()
                        personalNextCourse.value = it
                        if (persistForWidget) {
                            snapshotStore?.savePersonalNext(it)
                            appContext?.let { context -> WidgetUpdater.updateAll(context) }
                        }
                    }
                    .onFailure { error ->
                        if (error is CancellationException) throw error
                        // REST 暂时不可用：短时间内沿用上次结果；过期后清空，让通知与小组件回退到当前班级状态，
                        // 而不是继续展示早已结束的课程或直接清空课堂通知。
                        if (isPersonalScheduleStale(lastSuccessAt, SystemClock.elapsedRealtime()) &&
                            personalNextCourse.value != null
                        ) {
                            personalNextCourse.value = null
                            if (persistForWidget) {
                                snapshotStore?.clearPersonalNext()
                                appContext?.let { context -> WidgetUpdater.updateAll(context, force = true) }
                            }
                        }
                    }
                delay(PersonalScheduleRefreshMs)
            }
        }
    }

    /** 缓存归属随登录身份与当前班级变化；变化时旧账号、旧服务器或旧班级的小组件内容立即清除。 */
    private fun bindWidgetOwner(user: UserProfile?, classId: String?) {
        val store = snapshotStore ?: return
        if (store.bindOwner(widgetCacheOwner(restBaseUrl(), user, classId))) {
            appContext?.let { WidgetUpdater.updateAll(it, force = true) }
        }
    }

    /**
     * 换课申请的个人通知不经过当前班级：在线时由 user_notify 实时到达，
     * 这里再按短周期补齐未读通知并刷新待处理数，覆盖局域网直连或断线期间漏掉的通知。
     */
    private fun syncSwapInbox(user: UserProfile?) {
        val userId = user?.takeIf { it.id.isNotBlank() && it.has(Protocol.PERMISSION_REQUEST_SWAP) }?.id
        if (userId == swapInboxUserId && swapInboxJob?.isActive == true) return
        swapInboxJob?.cancel()
        swapInboxUserId = userId
        pendingSwapCount.value = 0
        if (userId == null) return
        swapInboxJob = scope.launch {
            while (kotlin.coroutines.coroutineContext.isActive) {
                refreshSwapInbox()
                delay(PersonalScheduleRefreshMs)
            }
        }
    }

    /** 拉取未读个人通知与待处理换课数；换课页操作后也会调用以立即刷新角标。 */
    suspend fun refreshSwapInbox() {
        runCatching { AdminApi.notifications(unreadOnly = true) }
            .onSuccess { items -> items.sortedBy { it.createdAt }.forEach { userNotifications.tryEmit(it) } }
        runCatching { AdminApi.swapRequests("incoming") }
            .onSuccess { items -> pendingSwapCount.value = items.count { it.canDecide || it.canRevoke } }
    }

    /** 班级内的有效权限；服务端未下发班级信息（旧服务端）时回退到全局权限。 */
    fun hasClassPermission(permission: Int): Boolean {
        val classContext = classes.value.firstOrNull { it.id == currentClassId.value }
        val effective = classContext?.effectivePermissions ?: currentUser.value?.permissions ?: 0
        return effective and permission == permission
    }

    /**
     * 切换当前班级：立即展示该班已缓存的数据，之后的命令都会路由到新班级。
     * 局域网连接只承载所连插件那个班级的数据，换班后必须改连新班插件或云端。
     */
    fun switchClass(classId: String) {
        if (classes.value.none { it.id == classId }) return
        val previousClassId = currentClassId.value
        if (previousClassId == classId) return
        // 拉取任务属于原班级，不能在新班级继续显示或触发旧任务的超时提示。
        schedulePullJob?.cancel()
        schedulePullState.value = SchedulePullState.Idle
        currentClassId.value = classId
        bindWidgetOwner(currentUser.value, classId)
        publishClassData(classId)
        lastCapabilitiesSync?.let { availableCapabilities.value = effectiveCapabilities(it, classId) }

        val current = desiredSettings
        // 旧版本保存的直连候选不带班级：它们只会是换班前那个班级的插件，补上归属以免换班后又连回去。
        var updated = current?.let {
            it.copy(
                selectedClassId = classId,
                lanClassId = it.lanClassId.ifBlank { previousClassId.orEmpty() },
            )
        }
        pluginNetworkInfos[classId]?.let { info -> updated = updated?.let { mergePluginNetworkInfo(it, info) } }
        updated?.let { next ->
            desiredSettings = next
            if (next != current) discoveredSettings.tryEmit(next)
        }
        when (state.value) {
            // 正在进行的尝试仍按旧班级的直连候选连接，同样要改用新班级的候选重来。
            State.LanConnected, State.Connecting -> updated?.let(::reconnectNow)
            // 云端连接本就接收所有可访问班级的推送；只有确实没有该班课表且有权拉取时才请求插件重新生成。
            State.CloudConnected ->
                if (schedule.value == null && currentUser.value?.canPullScheduleFor(classId) == true) requestSchedulePull()
            else -> Unit
        }
    }

    /** 当前展示的数据若不带班级标识（局域网插件、旧服务端），在缓存中归入 [classId]，已有缓存时不覆盖。 */
    private fun adoptUnattributedData(classId: String) {
        snapshot.value?.takeIf { it.classId == null }?.let { classSnapshots.putIfAbsent(classId, it) }
        schedule.value?.takeIf { it.classId == null }?.let { classSchedules.putIfAbsent(classId, it) }
        extensions.value.takeIf { it.isNotEmpty() && it.first().classId == null }
            ?.let { classExtensions.putIfAbsent(classId, it) }
    }

    private fun publishClassData(classId: String?) {
        snapshot.value = classId?.let(classSnapshots::get)
        schedule.value = classId?.let(classSchedules::get)
        extensions.value = classId?.let(classExtensions::get).orEmpty()
        snapshot.value?.let { persistWidgetSnapshot(it, force = true) }
        schedule.value?.let { snapshotStore?.saveSchedule(it) }
        appContext?.let { WidgetUpdater.updateAll(it, force = true) }
    }

    private fun persistWidgetSnapshot(value: ClassStateSnapshot, force: Boolean = false) {
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastWidgetSnapshotPersistAt < 15_000L) return
        lastWidgetSnapshotPersistAt = now
        snapshotStore?.save(value)
    }

    fun scanLanPlugins() {
        discoveryJob?.cancel()
        lanPlugins.value = emptyList()
        if (!localNetworkAllowed()) {
            lanDiscoveryScanning.value = false
            lanDiscoveryStatus.value = LocalNetworkPermissionMessage
            return
        }
        lanDiscoveryStatus.value = "正在扫描同一局域网中的 RemoteCI 插件…"
        lanDiscoveryScanning.value = true
        discoveryJob = scope.launch {
            try {
                val found = lanDiscoveryClient.scan()
                lanPlugins.value = found
                lanDiscoveryStatus.value = if (found.isEmpty())
                    "未发现插件，可检查 Wi-Fi、UDP ${Protocol.LAN_DISCOVERY_PORT} 防火墙或手动填写地址"
                else
                    "发现 ${found.size} 台插件，请选择要连接的电脑"
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                lanDiscoveryStatus.value = "扫描失败：${error.message ?: "网络不可用"}"
            } finally {
                lanDiscoveryScanning.value = false
            }
        }
    }

    suspend fun loadLanBootstrap(
        settings: WatchSettings,
        candidate: LanPluginCandidate,
    ): WatchSettings? {
        lanDiscoveryStatus.value = "正在连接 ${candidate.instanceName}…"
        return try {
            if (!localNetworkAllowed()) throw IOException(LocalNetworkPermissionMessage)
            val bootstrap = lanDiscoveryClient.fetchBootstrap(candidate)
            val updated = mergeLanBootstrapInfo(settings, candidate, bootstrap)
            lanPlugins.value = emptyList()
            // 局域网发现与 bootstrap 均无认证：明文 HTTP 提示窃听风险；
            // 首次引导或与上次实际使用的地址不同时都强制二次确认（TOFU），防止伪造引导诱导输入密码。
            val insecure = updated.cloudServerUrl.startsWith("http://")
            val changed = bootstrapUrlChanged(settings.cloudServerUrl, updated.cloudServerUrl)
            if (changed) {
                lanBootstrapPending.value = candidate to updated
                val httpWarning = if (insecure) "（且为明文 HTTP）" else ""
                lanDiscoveryStatus.value = if (settings.cloudServerUrl.isBlank())
                    "已发现云服务器：${updated.cloudServerUrl}。请点击确认后再登录" + httpWarning
                else
                    "云服务器与上次使用的不同：${updated.cloudServerUrl}。请再次点击确认，否则不要登录" + httpWarning
                return null
            }
            lanBootstrapPending.value = null
            lanDiscoveryStatus.value = "已获取云服务器：${updated.cloudServerUrl}，确认后请点安全登录" +
                if (insecure) "（明文 HTTP，请确认网络可信）" else ""
            updated
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            lanDiscoveryStatus.value = "连接插件失败：${error.message ?: "未返回云服务器信息"}"
            null
        }
    }

    /** 用户对 TOFU 候选二次确认；返回待应用的设置（界面层持久化）。 */
    fun confirmLanBootstrap(): WatchSettings? {
        val pending = lanBootstrapPending.value ?: return null
        lanBootstrapPending.value = null
        val updated = pending.second
        val insecure = updated.cloudServerUrl.startsWith("http://")
        lanDiscoveryStatus.value = "已确认使用 ${updated.cloudServerUrl}，请点安全登录" +
            if (insecure) "（明文 HTTP）" else ""
        return updated
    }

    /** password 或扫码登录票据仅用于本次 HTTPS 认证；成功后只保存 Keystore 加密的设备会话密钥。 */
    @Synchronized
    fun connect(settings: WatchSettings, password: String? = null, mobileLoginTicket: String? = null) {
        check(::sessions.isInitialized) { "ConnectionManager 尚未初始化" }
        discoveryJob?.cancel()
        lanDiscoveryScanning.value = false
        val attempt = generation.incrementAndGet()
        desiredSettings = settings
        activeJob?.cancel()
        refreshJob?.cancel()
        reconnectJob?.cancel()
        volumeJob?.cancel()
        closeActiveSocket("switch")
        protocolRejected = false
        lastHandshakeError = null
        state.value = State.Connecting
        serverVersion.value = null
        lastCommandResult.value = null
        pendingPasswordSetup.value = null
        extensions.value = emptyList()
        // 重新连接后以服务端下发的设置快照为准，未同步前 UI 按默认开启处理。
        this@ConnectionManager.settings.value = null
        // 冷启动时恢复上次选择的班级：局域网镜像不带班级列表，直连数据要归到正确的班级。
        if (currentClassId.value == null) {
            settings.selectedClassId.takeIf(String::isNotBlank)?.let { currentClassId.value = it }
        }
        // 扫码登录必须先到云端用一次性票据换取设备会话，再走后续连接流程。
        val plan = if (mobileLoginTicket != null) {
            ConnectionPlan(
                bootstrapCloudAuthentication = true,
                preferLanAfterCloudAuthentication = false,
                allowCloudFallback = true,
            )
        } else planConnection(settings, password)

        activeJob = scope.launch {
            try {
                if (plan.bootstrapCloudAuthentication) {
                    val auth = if (mobileLoginTicket != null) loginWithMobileTicket(settings, mobileLoginTicket) else loginCloud(settings, password ?: "")
                    if (auth.passwordPending == true) {
                        // 待激活账号：展示首登设置密码表单，设置成功后由界面重新登录。
                        pendingPasswordSetup.value =
                            PasswordSetupChallenge(settings.username.trim(), auth.setupToken.orEmpty())
                        state.value = State.Error("首次登录，请设置新密码")
                        return@launch
                    }
                    val session = sessions.load() ?: throw MissingSessionException()
                    if (plan.preferLanAfterCloudAuthentication && shouldTryLan(settings)) {
                        // 先直连一次；插件镜像尚未同步时走云端，后续地址上报会触发重新直连。
                        if (connectLan(settings, session, attempt)) {
                            keepRestTokenFresh(attempt)
                            return@launch
                        }
                    }
                    if (!plan.allowCloudFallback)
                        throw IOException(lastHandshakeError ?: "云端认证已完成，但局域网连接失败且云端中转已关闭")
                    connectCloud(settings, attempt)
                    return@launch
                }

                val saved = sessions.load() ?: throw MissingSessionException()
                if (settings.username.isNotBlank() && saved.username != settings.username)
                    throw MissingSessionException()

                // 已保存会话优先走局域网，和手表保持相同的低延迟路径。
                // 令牌刷新只服务于 WebUI 管理 API，不能阻塞本地 WebSocket 的首屏连接。
                if (shouldTryLan(settings) && connectLan(settings, saved, attempt)) {
                    // 局域网连接可用后再在后台准备短期令牌，人员、设置、备份等 WebUI 管理接口仍然可用。
                    if (plan.allowCloudFallback) keepRestTokenFresh(attempt)
                    return@launch
                }
                if (!plan.allowCloudFallback) {
                    throw IOException(
                        lastHandshakeError ?: if (localNetworkAllowed()) "局域网连接失败" else LocalNetworkPermissionMessage,
                    )
                }
                connectCloud(settings, attempt)
            } catch (error: CancellationException) {
                // 旧连接任务被新连接取消：直接透出，不得用旧任务的取消异常
                // 覆盖新任务刚写入的 Connecting 状态或清空用户信息。
                throw error
            } catch (error: Exception) {
                // 不可取消的会话签发可能在任务被取代后才抛出，过期尝试不得改写新连接的状态。
                if (attempt != generation.get()) return@launch
                when (error) {
                    is AuthenticationException -> {
                        state.value = State.Error("用户名或密码错误")
                        serverVersion.value = null
                        applyUserProfile(null)
                        return@launch
                    }
                    is MissingSessionException -> {
                        state.value = State.Error("请先使用账号密码登录")
                        serverVersion.value = null
                        applyUserProfile(null)
                        return@launch
                    }
                }
                serverVersion.value = null
                // 连接过程中已写入的更具体错误（协议版本不兼容等）不被通用消息覆盖。
                val reason = (state.value as? State.Error)?.message ?: error.message ?: "连接失败"
                if (protocolRejected) {
                    state.value = State.Error(reason)
                } else {
                    // 网络抖动、服务器重启或认证限流都是暂时的：保留账号信息并按退避自动重试，
                    // 不能失败一次就停在“未连接”，直到用户手动重开应用。
                    state.value = State.Error("$reason，稍后自动重试")
                    scheduleReconnect(attempt)
                }
            }
        }
    }

    /** 应用进入后台：记录时刻，回到前台时据此判断连接是否可能已被系统冻结断开。 */
    fun onBackground() {
        backgroundSinceMs = System.currentTimeMillis()
    }

    /**
     * 应用回到前台：健康的连接保持不动，不再每次切回都断开重连；
     * 只有未连接、连接失败、后台过久或云端令牌即将到期时才立即重连。
     */
    fun onForeground(settings: WatchSettings) {
        if (!hasSavedSession()) return
        val since = backgroundSinceMs
        backgroundSinceMs = 0L
        val target = desiredSettings?.let(settings::withNetworkStateFrom) ?: settings
        when (val current = state.value) {
            State.Connecting -> Unit
            State.LanConnected, State.CloudConnected -> {
                val now = System.currentTimeMillis()
                val longBackground = since != 0L && now - since >= ForegroundRecheckMs
                val tokenExpiring = current == State.CloudConnected &&
                    issuedAuth?.let { it.expiresAtMs - now <= TokenReuseMarginMs } != false
                if (webSocket == null || longBackground || tokenExpiring) reconnectNow(target)
            }
            else -> reconnectNow(target)
        }
    }

    @Synchronized
    fun disconnect(clearUser: Boolean = false) {
        generation.incrementAndGet()
        desiredSettings = null
        activeJob?.cancel()
        refreshJob?.cancel()
        reconnectJob?.cancel()
        volumeJob?.cancel()
        schedulePullJob?.cancel()
        personalScheduleJob?.cancel()
        personalScheduleJob = null
        swapInboxJob?.cancel()
        swapInboxJob = null
        swapInboxUserId = null
        pendingSwapCount.value = 0
        personalScheduleUserId = null
        personalNextCourse.value = null
        schedulePullState.value = SchedulePullState.Idle
        closeActiveSocket("disconnect")
        serverVersion.value = null
        issuedAuth = null
        lastLanAdvertisement = null
        reconnectDelayMs = InitialReconnectDelayMs
        if (clearUser) {
            applyUserProfile(null)
            pluginNetworkInfos.clear()
            classSnapshots.clear()
            classSchedules.clear()
            classExtensions.clear()
            snapshot.value = null
            schedule.value = null
            snapshotStore?.clear()
            appContext?.let { WidgetUpdater.updateAll(it, force = true) }
        }
        extensions.value = emptyList()
        this@ConnectionManager.settings.value = null
        state.value = State.Idle
    }

    fun logout(settings: WatchSettings) {
        val token = issuedAuth?.response?.accessToken
        val saved = sessions.load()
        if (token != null && saved != null) {
            scope.launch {
                runCatching {
                    authHttp.newCall(
                        Request.Builder()
                            .url("${normalizeServerUrl(settings.cloudServerUrl)}/api/auth/logout")
                            .header("Authorization", "Bearer $token")
                            .post(ByteArray(0).toRequestBody(null))
                            .build(),
                    ).execute().close()
                }
            }
        }
        sessionEpoch.incrementAndGet()
        sessions.clear()
        disconnect(clearUser = true)
    }

    fun sendScheduleChange(request: ScheduleChangeRequest) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_CHANGE_SCHEDULE, scheduleChange = request),
            Protocol.PERMISSION_MANAGE_SCHEDULE,
        )
    }

    /** 请求当前连接对应的插件重新生成课表；所有端共享任务状态，运行中不会重复发送。 */
    fun requestSchedulePull() {
        if (currentUser.value?.canPullScheduleFor(currentClassId.value) != true) {
            schedulePullState.value = SchedulePullState.Error("没有本班课表拉取权限，请等待插件同步课表")
            return
        }
        if (schedulePullState.value is SchedulePullState.Pulling) {
            schedulePullState.value = SchedulePullState.Pulling("已有课表拉取或推送任务正在执行，请稍候")
            return
        }
        val socket = webSocket
        if (socket == null || state.value !in setOf(State.LanConnected, State.CloudConnected)) {
            schedulePullState.value = SchedulePullState.Error("当前未连接，无法拉取课表")
            return
        }

        lastCommandResult.value = null
        val envelope = schedulePullEnvelope(currentClassId.value)
        schedulePullState.value = SchedulePullState.Pulling("正在连接插件…")
        if (!socket.send(encodeEnvelope(envelope))) {
            schedulePullState.value = SchedulePullState.Error("发送拉取请求失败，请重试")
            return
        }

        armSchedulePullTimeout("请求已发送，正在等待插件返回最新课表…")
    }

    fun teacherComing() {
        sendCommand(
            CommandMessage(command = Protocol.CMD_TEACHER_COMING),
            Protocol.PERMISSION_TEACHER_COMING,
        )
    }

    fun sendNotification(
        title: String,
        message: String,
        isNotificationEffectEnabled: Boolean,
        isNotificationSoundEnabled: Boolean,
        isSpeechEnabled: Boolean,
        isNotificationTopmostEnabled: Boolean = false,
        durationSeconds: Int? = null,
        repeatCounts: Int? = null,
        isRollingEnabled: Boolean = false,
    ) {
        sendCommand(
            CommandMessage(
                command = Protocol.CMD_SEND_NOTIFICATION,
                notification = NotificationRequest(
                    title = title.trim(),
                    message = message.trim(),
                    isNotificationEffectEnabled = isNotificationEffectEnabled,
                    isNotificationSoundEnabled = isNotificationSoundEnabled,
                    isSpeechEnabled = isSpeechEnabled,
                    isNotificationTopmostEnabled = isNotificationTopmostEnabled,
                    durationSeconds = durationSeconds,
                    repeatCounts = repeatCounts,
                    isRollingEnabled = isRollingEnabled,
                ),
            ),
            Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )
    }

    fun clearNotifications() {
        sendCommand(
            CommandMessage(command = Protocol.CMD_CLEAR_NOTIFICATIONS),
            Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )
    }

    fun setMainMenuVisible(visible: Boolean) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_SET_MAIN_MENU_VISIBILITY, mainMenuVisible = visible),
            Protocol.PERMISSION_MAIN_MENU_CONTROL,
        )
    }

    fun sendPowerAction(action: Int) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_POWER, powerAction = action),
            Protocol.PERMISSION_POWER_CONTROL,
        )
    }

    fun setVolume(level: Int) {
        // 表冠会高频产生事件，只发送短时间内的最后一个值，避免淹没 WebSocket 命令队列。
        volumeJob?.cancel()
        volumeJob = scope.launch {
            delay(80)
            sendCommand(
                CommandMessage(
                    command = Protocol.CMD_VOLUME,
                    volume = VolumeControlRequest(level = level.coerceIn(0, 100)),
                ),
                Protocol.PERMISSION_POWER_CONTROL,
            )
        }
    }

    fun setMuted(muted: Boolean) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_VOLUME, volume = VolumeControlRequest(muted = muted)),
            Protocol.PERMISSION_POWER_CONTROL,
        )
    }

    fun runExtension(extension: ExtensionDefinition, args: Map<String, String?> = emptyMap()) {
        val user = currentUser.value
        val allowed = user?.allowedExtensionIds
        if (!hasClassPermission(Protocol.PERMISSION_RUN_EXTENSIONS) ||
            (allowed != null && extension.id !in allowed)
        ) {
            lastCommandResult.value = CommandResult(false, "FORBIDDEN", "权限不足或扩展未开放")
            return
        }
        sendCommand(
            CommandMessage(
                command = Protocol.CMD_RUN_EXTENSION,
                extensionId = extension.id,
                extensionArgs = args,
            ),
            Protocol.PERMISSION_RUN_EXTENSIONS,
        )
    }

    private fun sendCommand(command: CommandMessage, requiredPermission: Int) {
        // 权限按当前班级计算（班主任只在所属班级有管理权限），命令随班级路由。
        if (!hasClassPermission(requiredPermission)) {
            lastCommandResult.value = CommandResult(false, "FORBIDDEN", "权限不足")
            return
        }
        lastCommandResult.value = null
        sendEnvelope(
            Envelope(
                type = Protocol.TYPE_COMMAND,
                messageId = newMessageId(),
                payload = json.encodeToJsonElement(
                    CommandMessage.serializer(),
                    command.copy(classId = command.classId ?: currentClassId.value),
                ),
            ),
        )
    }

    /** 直连前置条件：开关、权限、候选属于当前班级，且本机确实连着可能到达局域网的网络。 */
    private fun shouldTryLan(settings: WatchSettings): Boolean {
        val hosts = lanEndpointHosts(settings)
        return settings.lanConnectionEnabled && hosts.isNotEmpty() && localNetworkAllowed() &&
            lanCandidatesBelongTo(settings, currentClassId.value) &&
            (hosts.any { it.equals("localhost", ignoreCase = true) || isLoopbackHost(it) } || hasLocalNetworkTransport())
    }

    /** 只有蜂窝网络时私网地址必然不可达：直接走云端，省去每次重连的局域网超时。 */
    private fun hasLocalNetworkTransport(): Boolean {
        val manager = appContext?.getSystemService(ConnectivityManager::class.java) ?: return true
        return runCatching {
            @Suppress("DEPRECATION")
            val networks = manager.allNetworks
            networks.isEmpty() || networks.any { network ->
                manager.getNetworkCapabilities(network)?.let { capabilities ->
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                        capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ||
                        capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
                } == true
            }
        }.getOrDefault(true)
    }

    private suspend fun connectLan(
        settings: WatchSettings,
        session: PersistedDeviceSession,
        attempt: Int,
    ): Boolean {
        if (!localNetworkAllowed()) return false
        // 明文 ws:// 直连只允许私网/环回主机，公网候选一律跳过。
        val hosts = lanEndpointHosts(settings).filter(::isCleartextSafeHost)
        if (hosts.isEmpty()) return false
        // 插件会上报虚拟网卡等多块网卡地址：并发探测、先认证者胜出，
        // 首屏等待不再随候选数量线性增长（逐个超时时可拖到十几秒）。
        val host = coroutineScope {
            val winner = CompletableDeferred<String?>()
            val probes = hosts.map { candidate ->
                launch {
                    val outcome = connectWebSocket(
                        url = lanWebSocketUrl(candidate, settings.lanPort),
                        successState = State.LanConnected,
                        session = session,
                        attempt = attempt,
                        client = lanOkHttp,
                        handshakeTimeoutMs = LanAuthHandshakeTimeoutMs,
                    )
                    if (outcome == SocketOutcome.Authenticated) winner.complete(candidate)
                }
            }
            launch {
                probes.joinAll()
                winner.complete(null)
            }
            winner.await().also { coroutineContext.cancelChildren() }
        } ?: return false

        val updated = settings.copy(
            lanHost = host,
            lanHostCandidates = listOf(host) + lanEndpointHosts(settings).filterNot { it == host },
        )
        desiredSettings = updated
        if (updated != settings) discoveredSettings.tryEmit(updated)
        return true
    }

    /**
     * 首登设置密码：凭空密码登录下发的一次性令牌补设密码。
     * 成功后清除挑战并返回，界面应携带新密码重新登录。
     */
    suspend fun setupInitialPassword(newPassword: String): Boolean {
        val challenge = pendingPasswordSetup.value ?: return false
        return withContext(Dispatchers.IO) {
            val body = json.encodeToString(
                SetupPasswordRequest(
                    username = challenge.username,
                    setupToken = challenge.setupToken,
                    newPassword = newPassword,
                ),
            )
            val request = Request.Builder()
                .url("${restBaseUrl()!!}/api/auth/setup-password")
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()
            authHttp.newCall(request).execute().use { response ->
                if (response.code == 401) throw AuthenticationException()
                if (!response.isSuccessful) throw IOException("设置密码失败（HTTP ${response.code}）")
                pendingPasswordSetup.value = null
                true
            }
        }
    }

    private suspend fun connectCloud(settings: WatchSettings, attempt: Int) {
        requireCloudServerUrl(settings.cloudServerUrl)
        var issued = cloudAuth(settings)
        // 服务端令牌是标准 Base64，含 +/；不编码时 + 会被服务端解码成空格导致 401。
        var outcome = connectWebSocket(
            cloudWebSocketUrl(settings.cloudServerUrl, issued.response.accessToken),
            State.CloudConnected,
            null,
            attempt,
        )
        if (outcome == SocketOutcome.Unauthorized && attempt == generation.get()) {
            // 复用的令牌已被服务端作废（其他入口刷新、管理员吊销等）：强制换新后只重试一次。
            issued = cloudAuth(settings, rejectedToken = issued.response.accessToken)
            outcome = connectWebSocket(
                cloudWebSocketUrl(settings.cloudServerUrl, issued.response.accessToken),
                State.CloudConnected,
                null,
                attempt,
            )
        }
        if (outcome != SocketOutcome.Authenticated) throw IOException("云端连接失败")
        // 收到插件网络信息后可能已经切换到新的局域网连接尝试，旧云端协程不得再覆盖刷新任务。
        if (attempt != generation.get()) return
        keepCloudConnectionFresh(attempt)
    }

    private suspend fun loginCloud(settings: WatchSettings, password: String): AuthResponse =
        sessionMutex.withLock {
            withContext(NonCancellable) {
                val issued = postAuth(
                    settings,
                    "/api/auth/login",
                    json.encodeToString(LoginRequest(settings.username.trim(), password, "Android · ${Build.MANUFACTURER} ${Build.MODEL}")),
                )
                // 首登待设密码的账号不下发可用会话，不能覆盖已保存的会话。
                if (issued.response.passwordPending != true) persist(issued)
                issued.response
            }
        }

    /** 扫码登录：用 WebUI 二维码中的一次性票据换取设备会话，成功后复用正常连接流程。 */
    private suspend fun loginWithMobileTicket(settings: WatchSettings, ticket: String): AuthResponse =
        sessionMutex.withLock {
            withContext(NonCancellable) {
                val issued = postAuth(
                    settings,
                    "/api/auth/mobile-login",
                    json.encodeToString(MobileLoginRequest(ticket, "Android · ${Build.MANUFACTURER} ${Build.MODEL}")),
                )
                persist(issued)
                issued.response
            }
        }

    /**
     * 取得可用的云端访问令牌：仍有余量的令牌直接复用（重连不再多一次 HTTP 往返，也不会触发认证限流），
     * 否则用设备会话刷新。[rejectedToken] 是已确认失效的令牌，即使未到期也不再复用。
     */
    private suspend fun cloudAuth(settings: WatchSettings, rejectedToken: String? = null): IssuedAuth =
        sessionMutex.withLock {
            requireCloudServerUrl(settings.cloudServerUrl)
            val serverUrl = normalizeServerUrl(settings.cloudServerUrl)
            val saved = sessions.load() ?: throw MissingSessionException()
            val reusable = issuedAuth?.takeIf {
                it.response.accessToken != rejectedToken &&
                    it.usableFor(serverUrl, saved.deviceSessionId, System.currentTimeMillis())
            }
            if (reusable != null) return@withLock reusable
            val epoch = sessionEpoch.get()
            // 服务端一旦处理刷新就已轮换密钥：即使发起方此时被新连接取消，也必须把新会话落盘。
            withContext(NonCancellable) {
                val issued = try {
                    postAuth(
                        settings,
                        "/api/auth/refresh",
                        json.encodeToString(RefreshSessionRequest(saved.deviceSessionId, saved.deviceSecret)),
                    )
                } catch (_: AuthenticationException) {
                    if (sessionEpoch.get() == epoch) {
                        sessions.clear()
                        // 设备会话被吊销或过期：本机缓存的课堂与日程不再属于可证明的登录身份。
                        snapshotStore?.clear()
                        appContext?.let { WidgetUpdater.updateAll(it, force = true) }
                    }
                    issuedAuth = null
                    throw MissingSessionException()
                }
                if (sessionEpoch.get() != epoch) throw MissingSessionException()
                persist(issued)
                issued
            }
        }

    private suspend fun postAuth(settings: WatchSettings, path: String, bodyJson: String): IssuedAuth =
        withContext(Dispatchers.IO) {
            requireCloudServerUrl(settings.cloudServerUrl)
            if (!localNetworkAllowed() && isLocalServerUrl(settings.cloudServerUrl)) {
                throw IOException(LocalNetworkPermissionMessage)
            }
            val serverUrl = normalizeServerUrl(settings.cloudServerUrl)
            val request = Request.Builder()
                .url("$serverUrl$path")
                .post(bodyJson.toRequestBody("application/json".toMediaType()))
                .build()
            authHttp.newCall(request).execute().use { response ->
                if (response.code == 401) throw AuthenticationException()
                if (response.code == 429) throw IOException("登录请求过于频繁")
                if (!response.isSuccessful) throw IOException("登录服务返回 HTTP ${response.code}")
                val auth = json.decodeFromString(AuthResponse.serializer(), response.body.string())
                val now = System.currentTimeMillis()
                IssuedAuth(
                    auth,
                    serverUrl,
                    localAccessExpiryMillis(auth.accessExpiresAt, response.headers.getDate("Date")?.time, now),
                )
            }
        }

    private fun persist(issued: IssuedAuth) {
        val auth = issued.response
        sessions.save(
            PersistedDeviceSession(
                username = auth.user.username,
                deviceSessionId = auth.deviceSessionId,
                deviceSecret = auth.deviceSecret,
                deviceExpiresAt = auth.deviceExpiresAt,
            ),
        )
        issuedAuth = issued
        applyUserProfile(auth.user)
    }

    /**
     * 局域网连接期间为 WebUI 管理接口保持短期令牌：只在后台换新令牌，不再为续期断开直连。
     * 会话已失效时停止，插件镜像同步后会断开直连，届时走正常的重新登录提示。
     */
    private fun keepRestTokenFresh(attempt: Int) {
        refreshJob?.cancel()
        refreshJob = scope.launch {
            while (attempt == generation.get()) {
                val settings = desiredSettings ?: return@launch
                val issued = try {
                    cloudAuth(settings)
                } catch (error: CancellationException) {
                    throw error
                } catch (_: MissingSessionException) {
                    return@launch
                } catch (_: Exception) {
                    null
                }
                delay(issued?.let(::untilRefresh) ?: AccessRefreshRetryMs)
            }
        }
    }

    /**
     * 云端连接的有效期与访问令牌绑定：到期前先用新令牌建立并认证新连接，再退役旧连接，
     * 界面不出现断线；新连接建立失败时退回完整重连。
     */
    private fun keepCloudConnectionFresh(attempt: Int) {
        refreshJob?.cancel()
        refreshJob = scope.launch {
            while (attempt == generation.get()) {
                val current = issuedAuth ?: return@launch
                delay(untilRefresh(current))
                if (attempt != generation.get() || state.value != State.CloudConnected) return@launch
                val settings = desiredSettings ?: return@launch
                val previous = webSocket ?: return@launch
                val fresh = try {
                    cloudAuth(settings, rejectedToken = current.response.accessToken)
                } catch (error: CancellationException) {
                    throw error
                } catch (_: MissingSessionException) {
                    // 会话已被吊销：服务端随后会断开旧连接，断线重连流程会提示重新登录。
                    return@launch
                } catch (_: Exception) {
                    // 暂时无法刷新：旧令牌尚有余量，稍后再试。
                    delay(AccessRefreshRetryMs)
                    continue
                }
                if (attempt != generation.get()) return@launch
                val outcome = connectWebSocket(
                    cloudWebSocketUrl(settings.cloudServerUrl, fresh.response.accessToken),
                    State.CloudConnected,
                    null,
                    attempt,
                    replaceable = previous,
                )
                if (outcome != SocketOutcome.Authenticated) {
                    // 旧令牌已在刷新时作废，旧连接很快会被服务端断开：直接走完整重连。
                    if (attempt == generation.get()) reconnectNow(settings)
                    return@launch
                }
            }
        }
    }

    private fun untilRefresh(issued: IssuedAuth): Long =
        (issued.expiresAtMs - System.currentTimeMillis() - TokenReuseMarginMs).coerceAtLeast(MinAccessRefreshDelayMs)

    private suspend fun connectWebSocket(
        url: String,
        successState: State,
        session: PersistedDeviceSession?,
        attempt: Int,
        client: OkHttpClient = okHttp,
        handshakeTimeoutMs: Long = AuthHandshakeTimeoutMs,
        replaceable: WebSocket? = null,
    ): SocketOutcome = try {
        // 认证阶段限时：故障或恶意对端完成握手后从不回 auth_state 时（readTimeout=0 + ping 保活
        // 会让死连接永久存活），超时后按连接失败走外层回退，而不是永久停在“连接中”。
        withTimeout(handshakeTimeoutMs) {
            awaitAuthenticatedSocket(url, successState, session, attempt, client, replaceable)
        }
    } catch (_: TimeoutCancellationException) {
        SocketOutcome.Failed
    }

    private suspend fun awaitAuthenticatedSocket(
        url: String,
        successState: State,
        session: PersistedDeviceSession?,
        attempt: Int,
        client: OkHttpClient,
        replaceable: WebSocket?,
    ): SocketOutcome = suspendCancellableCoroutine { continuation ->
        // 只有认证成功并成为活跃连接的 socket 断开后才自动重连；握手失败、被同一次尝试中
        // 其他候选抢先或令牌轮换后退役的 socket 由外层流程处理，否则每次失败都会调度一次全量重连形成风暴。
        val authenticated = AtomicBoolean(false)
        val fromLan = session != null
        fun finish(outcome: SocketOutcome) {
            if (continuation.isActive) continuation.resume(outcome)
        }
        val listener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                // 超时或已切换到新尝试：不得让过期 socket 继续握手。
                if (attempt != generation.get() || !continuation.isActive) webSocket.close(1000, "superseded")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (attempt != generation.get()) {
                    webSocket.close(1000, "superseded")
                    finish(SocketOutcome.Failed)
                    return
                }
                // 已认证却不再是活跃连接（并发探测的落选者、轮换后的旧连接）：消息一律丢弃。
                if (authenticated.get() && ConnectionManager.webSocket !== webSocket) return
                val envelope = runCatching { json.decodeFromString(Envelope.serializer(), text) }.getOrNull() ?: return
                if (envelope.protocolVersion != Protocol.VERSION) {
                    if (!fromLan) protocolRejected = true
                    state.value = State.Error("协议版本不兼容，需要 v${Protocol.VERSION}")
                    webSocket.close(1008, "protocol")
                    finish(SocketOutcome.Failed)
                    return
                }
                if (envelope.type == Protocol.TYPE_AUTH_CHALLENGE && session != null) {
                    // 解码失败忽略该条消息；异常从 OkHttp 回调线程逃逸会直接击断连接。
                    val challenge = runCatching {
                        envelope.payload?.let { json.decodeFromJsonElement(AuthChallenge.serializer(), it) }
                    }.getOrNull() ?: return
                    val proof = createAuthProof(challenge, session)
                    webSocket.send(
                        encodeEnvelope(
                            Envelope(
                                type = Protocol.TYPE_AUTH_PROOF,
                                messageId = newMessageId(),
                                payload = json.encodeToJsonElement(AuthProof.serializer(), proof),
                            ),
                        ),
                    )
                    return
                }
                if (envelope.type == Protocol.TYPE_AUTH_STATE) {
                    val auth = decodePayload(envelope.payload, AuthState.serializer()) ?: return
                    if (authenticated.get()) {
                        onAuthStateRefreshed(webSocket, attempt, auth, fromLan)
                    } else if (auth.authenticated && auth.user != null && continuation.isActive &&
                        promote(webSocket, attempt, replaceable)
                    ) {
                        authenticated.set(true)
                        applyUserProfile(auth.user)
                        serverVersion.value = auth.serverVersion
                        sendCapabilitiesReport(webSocket)
                        state.value = successState
                        reconnectDelayMs = InitialReconnectDelayMs // 连接成功即复位退避。
                        finish(SocketOutcome.Authenticated)
                    } else {
                        if (!auth.authenticated) {
                            if (auth.errorCode == ProtocolVersionUnsupportedCode && !fromLan) {
                                protocolRejected = true
                                state.value = State.Error(auth.error ?: "协议版本不兼容，需要 v${Protocol.VERSION}")
                            } else if (fromLan) {
                                lastHandshakeError = auth.error
                            }
                        }
                        webSocket.close(1008, "auth failed")
                        finish(SocketOutcome.Failed)
                    }
                    return
                }
                // 认证完成前的其他消息无法确认归属，直接丢弃。
                if (!authenticated.get()) return
                handleEnvelope(envelope, fromLan)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                finish(if (response?.code == 401) SocketOutcome.Unauthorized else SocketOutcome.Failed)
                // OkHttp 中 onFailure 与 onClosed 互斥：异常断开（Wi-Fi 掉线、NAT 失效等）只会触发 onFailure。
                onSocketLost(webSocket, attempt)
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                finish(SocketOutcome.Failed)
                onSocketLost(webSocket, attempt)
            }
        }
        val socket = client.newWebSocket(Request.Builder().url(url).build(), listener)
        continuation.invokeOnCancellation { socket.close(1000, "cancelled") }
    }

    /**
     * 同一次连接尝试中最先完成认证的 socket 成为活跃连接；[replaceable] 是令牌轮换时允许被替换的旧连接，
     * 替换后立即关闭旧连接。其余情况下已有活跃连接时拒绝，落选者自行关闭。
     */
    private fun promote(socket: WebSocket, attempt: Int, replaceable: WebSocket?): Boolean {
        val previous = synchronized(socketLock) {
            if (attempt != generation.get()) return false
            val current = webSocket
            if (current != null && current !== replaceable) return false
            webSocket = socket
            current
        }
        previous?.close(1000, "rotated")
        return true
    }

    private fun closeActiveSocket(reason: String) {
        val previous = synchronized(socketLock) { webSocket.also { webSocket = null } }
        previous?.close(1000, reason)
    }

    /** 活跃连接断开：清理连接级状态并按退避自动重连；非活跃 socket（落选、退役、过期）的回调直接忽略。 */
    private fun onSocketLost(socket: WebSocket, attempt: Int) {
        synchronized(socketLock) {
            if (webSocket !== socket) return
            webSocket = null
        }
        serverVersion.value = null
        lastCapabilitiesSync = null
        availableCapabilities.value = Protocol.BASELINE_CAPABILITIES
        if (attempt != generation.get()) return
        if (schedulePullState.value is SchedulePullState.Pulling)
            finishSchedulePull(SchedulePullState.Error("连接已断开，课表任务结果未知"))
        // 不能停留在“已连接”：界面和命令入口需要知道当前连接已不可用。
        state.value = State.Error("连接已断开，正在自动重连…")
        scheduleReconnect(attempt)
    }

    /**
     * 已建立连接上收到的 auth_state：服务端周期复查或插件镜像更新时下发。
     * 认证失效（令牌被轮换或吊销、镜像中的会话过期）时不直接清空账号，而是丢弃缓存令牌并重连；
     * 会话确已吊销时，重连中的刷新会返回 401 并提示重新登录。
     */
    private fun onAuthStateRefreshed(socket: WebSocket, attempt: Int, auth: AuthState, fromLan: Boolean) {
        if (auth.authenticated && auth.user != null) {
            applyUserProfile(auth.user)
            serverVersion.value = auth.serverVersion
            return
        }
        if (auth.errorCode == ProtocolVersionUnsupportedCode) {
            if (!fromLan) protocolRejected = true
            state.value = State.Error(auth.error ?: "协议版本不兼容，需要 v${Protocol.VERSION}")
        }
        if (!fromLan) issuedAuth = null
        onSocketLost(socket, attempt)
        socket.close(1000, "reauthenticate")
    }

    private fun handleEnvelope(envelope: Envelope, fromLan: Boolean) {
        when (envelope.type) {
            Protocol.TYPE_STATE_PUSH -> {
                decodePayload(envelope.payload, ClassStateSnapshot.serializer())?.let { incoming ->
                    // 多班级：按班级缓存，只展示当前班级（或不带班级标识的局域网/旧服务端推送）。
                    val current = currentClassId.value
                    (incoming.classId ?: current)?.let { classSnapshots[it] = incoming }
                    if (incoming.classId == null || incoming.classId == current) {
                        snapshot.value = incoming
                        persistWidgetSnapshot(incoming)
                        appContext?.let { WidgetUpdater.updateAll(it) }
                    }
                }
            }
            Protocol.TYPE_SCHEDULE_SYNC -> {
                decodePayload(envelope.payload, ScheduleBundle.serializer())?.let { incoming ->
                    val current = currentClassId.value
                    (incoming.classId ?: current)?.let { classSchedules[it] = incoming }
                    if (incoming.classId == null || incoming.classId == current) {
                        schedule.value = incoming
                        snapshotStore?.saveSchedule(incoming)
                        appContext?.let { WidgetUpdater.updateAll(it, force = true) }
                        // 兼容未实现状态消息的旧插件：收到新课表本身也可作为成功终态。
                        if (schedulePullState.value is SchedulePullState.Pulling)
                            finishSchedulePull(SchedulePullState.Success("课表拉取完成，已使用插件最新课表"))
                    }
                }
            }
            Protocol.TYPE_SCHEDULE_SYNC_STATUS -> {
                decodePayload(envelope.payload, ScheduleSyncStatus.serializer())?.let { status ->
                    // 其他班级的课表任务（如服务端定时拉取）不影响当前班级的拉取状态。
                    if (status.classId == null || status.classId == currentClassId.value) applyScheduleSyncStatus(status)
                }
            }
            Protocol.TYPE_EXTENSIONS_SYNC -> {
                decodePayload(envelope.payload, ListSerializer(ExtensionDefinition.serializer()))?.let { incoming ->
                    val current = currentClassId.value
                    val classId = incoming.firstOrNull()?.classId
                    when {
                        classId != null -> {
                            classExtensions[classId] = incoming
                            if (classId == current) extensions.value = incoming
                        }
                        // 空清单不带班级标识：云端多班级时无法判断归属，忽略以免清空当前班级的扩展。
                        incoming.isNotEmpty() || fromLan || classes.value.size <= 1 -> {
                            current?.let { classExtensions[it] = incoming }
                            extensions.value = incoming
                        }
                    }
                }
            }
            Protocol.TYPE_SETTINGS_SYNC -> {
                decodePayload(envelope.payload, SettingsSync.serializer())?.let { settings.value = it }
            }
            Protocol.TYPE_PLUGIN_NETWORK_INFO -> {
                decodePayload(envelope.payload, PluginNetworkInfo.serializer())?.let { handlePluginNetworkInfo(it) }
            }
            Protocol.TYPE_CAPABILITIES_SYNC -> {
                decodePayload(envelope.payload, CapabilitiesSync.serializer())?.let { sync ->
                    lastCapabilitiesSync = sync
                    availableCapabilities.value = effectiveCapabilities(sync, currentClassId.value)
                }
            }
            Protocol.TYPE_EVENT_NOTIFY -> {
                decodePayload(envelope.payload, ClassEvent.serializer())?.let { incoming ->
                    if (incoming.classId == null || incoming.classId == currentClassId.value) events.tryEmit(incoming)
                }
            }
            Protocol.TYPE_USER_NOTIFY -> {
                // 个人通知面向账号本人，不按当前班级过滤：多班级任教的老师也能收到其他班的换课申请。
                decodePayload(envelope.payload, UserNotification.serializer())?.let { incoming ->
                    userNotifications.tryEmit(incoming)
                    if (incoming.kind == Swap.KIND_REQUESTED || incoming.kind == Swap.KIND_FORCED ||
                        incoming.kind == Swap.KIND_CANCELLED) scope.launch { refreshSwapInbox() }
                }
            }
            Protocol.TYPE_COMMAND_RESULT -> {
                decodePayload(envelope.payload, CommandResult.serializer())?.let {
                    lastCommandResult.value = it
                    envelope.replyToMessageId?.let { id -> voiceReplies[id]?.complete(it) }
                }
            }
        }
    }

    private fun sendCapabilitiesReport(socket: WebSocket) {
        val report = PeerCapabilities(
            softwareVersion = BuildConfig.VERSION_NAME,
            capabilities = Protocol.CURRENT_CAPABILITIES.toList(),
        )
        socket.send(
            json.encodeToString(
                Envelope.serializer(),
                Envelope(
                    type = Protocol.TYPE_PEER_CAPABILITIES,
                    messageId = newMessageId(),
                    payload = json.encodeToJsonElement(PeerCapabilities.serializer(), report),
                ),
            ),
        )
    }

    private fun applyScheduleSyncStatus(status: ScheduleSyncStatus) {
        when (status.state) {
            Protocol.SCHEDULE_TASK_RUNNING -> {
                armSchedulePullTimeout(status.message.ifBlank { "课表任务正在执行…" })
            }
            Protocol.SCHEDULE_TASK_BUSY -> {
                armSchedulePullTimeout(status.message.ifBlank { "已有课表任务正在执行，请稍候" })
            }
            Protocol.SCHEDULE_TASK_COMPLETED ->
                finishSchedulePull(SchedulePullState.Success(status.message.ifBlank { "课表同步完成" }))
            Protocol.SCHEDULE_TASK_FAILED ->
                finishSchedulePull(SchedulePullState.Error(status.message.ifBlank { "课表同步失败" }))
        }
    }

    private fun armSchedulePullTimeout(message: String) {
        schedulePullState.value = SchedulePullState.Pulling(message)
        schedulePullJob?.cancel()
        schedulePullJob = scope.launch {
            delay(SchedulePullTimeoutMs)
            if (schedulePullState.value is SchedulePullState.Pulling)
                finishSchedulePull(SchedulePullState.Error("等待课表任务完成超时"))
        }
    }

    private fun finishSchedulePull(result: SchedulePullState) {
        schedulePullJob?.cancel()
        schedulePullState.value = result
        if (result is SchedulePullState.Success) {
            schedulePullJob = scope.launch {
                delay(3_000)
                if (schedulePullState.value is SchedulePullState.Success)
                    schedulePullState.value = SchedulePullState.Idle
            }
        }
    }

    /** 反序列化失败的载荷忽略不处理：版本不兼容或脏数据不得从 OkHttp 回调线程逃逸击断连接。 */
    private fun <T> decodePayload(payload: JsonElement?, serializer: KSerializer<T>): T? =
        payload?.let { runCatching { json.decodeFromJsonElement(serializer, it) }.getOrNull() }

    private fun handlePluginNetworkInfo(info: PluginNetworkInfo) {
        info.classId?.let { pluginNetworkInfos[it] = info }
        // 多班级账号会收到每个可访问班级的插件地址：只有当前班级的插件能作为直连候选，
        // 否则会直连到别班插件、展示别班课表，多份地址交替到达时还会反复触发重连。
        val currentClass = currentClassId.value
        if (info.classId != null && currentClass != null && info.classId != currentClass) return
        val current = desiredSettings ?: return
        val updated = mergePluginNetworkInfo(current, info)
        desiredSettings = updated
        if (updated != current) discoveredSettings.tryEmit(updated)

        // 同一份不可达地址回退到云端后不反复重试；网卡或端口变化时才重新优先直连。
        val isNewAdvertisement = info != lastLanAdvertisement
        lastLanAdvertisement = info
        if (isNewAdvertisement && info.lanServerEnabled && state.value == State.CloudConnected && shouldTryLan(updated)) {
            connect(updated)
        }
    }

    @Synchronized
    private fun scheduleReconnect(attempt: Int) {
        if (desiredSettings == null) return
        val delayMs = reconnectDelayMs
        reconnectDelayMs = nextReconnectDelay(reconnectDelayMs)
        // 退避期间保留断线原因展示，connect() 真正发起时才切换到 Connecting。
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(delayMs)
            val settings = desiredSettings
            if (attempt == generation.get() && settings != null) connect(settings)
        }
    }

    /** 网络恢复、回到前台等确定性时机：跳过退避立即重连。 */
    private fun reconnectNow(settings: WatchSettings) {
        reconnectDelayMs = InitialReconnectDelayMs
        connect(settings)
    }

    private fun registerNetworkCallback(context: Context) {
        val manager = context.getSystemService(ConnectivityManager::class.java) ?: return
        runCatching {
            manager.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) = onDefaultNetworkAvailable(network)
            })
        }
    }

    /** 默认网络切换（Wi-Fi ↔ 蜂窝、VPN 开关）或断网后恢复时立即重连，不再等待 ping 超时或退避。 */
    private fun onDefaultNetworkAvailable(network: Network) {
        val previous = defaultNetwork
        defaultNetwork = network
        val settings = desiredSettings ?: return
        when (state.value) {
            // 旧连接绑定在原网络上，切换后已不可用：立即在新网络上重建。
            State.LanConnected, State.CloudConnected -> if (previous != null && previous != network) reconnectNow(settings)
            // 断线后正在退避等待：网络一恢复就重试。
            is State.Error -> if (reconnectJob?.isActive == true) reconnectNow(settings)
            else -> Unit
        }
    }

    private fun sendEnvelope(envelope: Envelope) {
        val sent = webSocket?.send(encodeEnvelope(envelope)) == true
        if (!sent) {
            lastCommandResult.value = CommandResult(false, "OFFLINE", "连接已断开，操作未发送")
        }
    }

    private fun newMessageId(): String = UUID.randomUUID().toString().replace("-", "")
    private class MissingSessionException : Exception()
    private class AuthenticationException : Exception()
}

/**
 * 当前班级可用能力 = 手机本地 ∩ 服务端 ∩ 该班主插件。
 * 新版服务端按班级下发插件能力：多班级部署中不能拿其他班级（或全局最早接入）的插件代替当前班级，
 * 否则当前班插件离线时仍显示控制项、或其他班插件离线时把当前班的控制项全部隐藏。
 */
internal fun effectiveCapabilities(sync: CapabilitiesSync, classId: String? = null): Set<String> {
    val perClass = sync.classPlugins
    val plugin = when {
        // 旧版服务端或局域网直连（插件只代表自己的班级）。
        perClass == null || classId == null -> sync.plugin
        else -> perClass.firstOrNull { it.classId.equals(classId, ignoreCase = true) }?.plugin
    }
    return Protocol.CURRENT_CAPABILITIES
        .intersect(sync.server.capabilities.toSet())
        .intersect(plugin?.capabilities?.toSet() ?: emptySet())
}

/** 当前班级权限；旧服务端或局域网镜像没有班级列表时回退到用户档案权限。 */
internal fun effectiveClassPermissions(
    classes: List<ClassSummary>,
    classId: String?,
    fallback: Int,
): Int = classes.firstOrNull { it.id == classId }?.effectivePermissions ?: fallback

internal data class ConnectionPlan(
    val bootstrapCloudAuthentication: Boolean,
    val preferLanAfterCloudAuthentication: Boolean,
    val allowCloudFallback: Boolean,
)

internal const val InitialReconnectDelayMs = 5_000L
internal const val PersonalScheduleRefreshMs = 60_000L

/** 个人日程连续刷新失败超过该时长后视为过期，通知与小组件回退到班级状态。 */
internal const val PersonalScheduleStaleMs = PersonalScheduleRefreshMs * 3

/** 需要轮询个人日程的账号：协议规定老师与班主任都拥有“我的日程”。 */
internal fun personalScheduleUserId(user: UserProfile?): String? =
    user?.takeIf { it.hasPersonalSchedule && it.id.isNotBlank() }?.id

/** 小组件与常驻通知是否以个人日程为主：只有老师；班主任默认展示本班状态。 */
internal fun widgetUsesPersonalSchedule(user: UserProfile?): Boolean = user?.isTeacher == true

internal fun isPersonalScheduleStale(lastSuccessAtMs: Long, nowMs: Long): Boolean =
    lastSuccessAtMs == 0L || nowMs - lastSuccessAtMs >= PersonalScheduleStaleMs

/** 小组件缓存的归属；未登录或没有账号 Id（不可证明的身份）时为 null，缓存随之清空。 */
internal fun widgetCacheOwner(serverUrl: String?, user: UserProfile?, classId: String?): SnapshotOwner? =
    user?.id?.takeIf(String::isNotBlank)?.let { SnapshotOwner(serverUrl.orEmpty(), it, classId) }
internal const val MaxReconnectDelayMs = 60_000L

/** 认证握手限时：对端只完成 WebSocket 握手但从不回 auth_state 时不得永久挂起。 */
internal const val AuthHandshakeTimeoutMs = 15_000L

/** 局域网认证应在一次短探测内失败，避免坏的虚拟网卡地址拖慢整个登录流程。 */
internal const val LanAuthHandshakeTimeoutMs = 4_000L

/** 手动拉取课表等待插件回传的最长时间，与 WebUI 的等待上限保持一致。 */
internal const val SchedulePullTimeoutMs = 15_000L

/** 访问令牌剩余有效期不足该值时不再复用，并在此时提前续期。 */
internal const val TokenReuseMarginMs = 2 * 60_000L

/** 续期最短间隔与失败重试间隔，避免令牌异常时高频请求认证接口（服务端按 IP 限流）。 */
internal const val MinAccessRefreshDelayMs = 30_000L
internal const val AccessRefreshRetryMs = 60_000L

/** 服务端未下发可解析的到期时间时，按 50 分钟估算（服务端默认 1 小时）。 */
internal const val DefaultAccessTtlMs = 50 * 60_000L

/** 后台超过该时长回到前台时重建连接：系统冻结后台进程后，服务端约 60–90 秒内会因收不到 pong 断开。 */
internal const val ForegroundRecheckMs = 30_000L

internal const val ProtocolVersionUnsupportedCode = "PROTOCOL_VERSION_UNSUPPORTED"

/** 断线重连的指数退避：每次翻倍并封顶 [MaxReconnectDelayMs]；连接成功后调用方复位为 [InitialReconnectDelayMs]。 */
internal fun nextReconnectDelay(currentMs: Long): Long = (currentMs * 2).coerceAtMost(MaxReconnectDelayMs)

internal fun normalizeServerUrl(url: String): String = url.trim().trimEnd('/')

/**
 * 访问令牌在本机时钟下的到期时刻：用服务器响应的 Date 头换算剩余有效期，
 * 手机时间偏慢时也能在服务端判定过期前续期；缺少 Date 头时退回本机时钟。
 */
internal fun localAccessExpiryMillis(accessExpiresAt: String, serverDateMillis: Long?, localNowMillis: Long): Long {
    val expiresAt = runCatching { OffsetDateTime.parse(accessExpiresAt).toInstant().toEpochMilli() }.getOrNull()
        ?: return localNowMillis + DefaultAccessTtlMs
    return localNowMillis + (expiresAt - (serverDateMillis ?: localNowMillis))
}

/** 直连候选来自哪个班级的插件未知（旧服务端、手动填写）时允许尝试；已知且不是当前班级时跳过，避免连到别班插件。 */
internal fun lanCandidatesBelongTo(settings: WatchSettings, classId: String?): Boolean =
    settings.lanClassId.isBlank() || classId.isNullOrBlank() || settings.lanClassId == classId

/** 界面持有的设置以用户可编辑字段为准；直连地址与班级选择由连接层发现，以连接层为准。 */
internal fun WatchSettings.withNetworkStateFrom(network: WatchSettings): WatchSettings = copy(
    lanHost = network.lanHost,
    lanHostCandidates = network.lanHostCandidates,
    lanPort = network.lanPort,
    lanClassId = network.lanClassId,
    selectedClassId = network.selectedClassId,
)

/**
 * 依据 protocol.md 构造局域网 HMAC 挑战证明：
 * 密钥 = SHA-256(deviceSecret)，消息 = `版本|challengeId|nonce|clientNonce|无横线小写 sessionId`。
 * [clientNonceBytes] 默认取 24 字节安全随机数，测试可注入固定值复现向量。
 */
internal fun createAuthProof(
    challenge: AuthChallenge,
    session: PersistedDeviceSession,
    // generateSeed 走熵采集路径，个别设备会长时间阻塞；nextBytes 非阻塞且足够安全。
    clientNonceBytes: ByteArray = ByteArray(24).also { java.security.SecureRandom().nextBytes(it) },
): AuthProof {
    val clientNonce = java.util.Base64.getEncoder().encodeToString(clientNonceBytes)
    val verifier = MessageDigest.getInstance("SHA-256").digest(session.deviceSecret.encodeToByteArray())
    val canonical = "${Protocol.VERSION}|${challenge.challengeId}|${challenge.nonce}|$clientNonce|" +
        session.deviceSessionId.replace("-", "").lowercase()
    val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(verifier, "HmacSHA256")) }
    return AuthProof(
        challengeId = challenge.challengeId,
        deviceSessionId = session.deviceSessionId,
        clientNonce = clientNonce,
        proof = java.util.Base64.getEncoder().encodeToString(mac.doFinal(canonical.encodeToByteArray())),
    )
}

/** 云端 WebSocket 地址；访问令牌必须做 URL 编码，服务端 Base64 令牌中的 + 在查询串里会被解码成空格。 */
internal fun cloudWebSocketUrl(cloudServerUrl: String, accessToken: String): String {
    val schemeUrl = normalizeServerUrl(cloudServerUrl)
        .replaceFirst("https://", "wss://")
        .replaceFirst("http://", "ws://")
    return "$schemeUrl/ws?token=${java.net.URLEncoder.encode(accessToken, Charsets.UTF_8.name())}&client=mobile"
}

/** 引导返回的云服务器地址与上次实际使用的地址不同（默认开发地址不算“用过”）时提示用户。 */
internal fun bootstrapUrlChanged(previous: String, current: String): Boolean {
    val old = previous.trim().trimEnd('/')
    val fresh = current.trim().trimEnd('/')
    if (fresh.isBlank()) return false
    // 开发用模拟器宿主机地址不参与变更比较。
    if (old.startsWith("http://10.0.2.2")) return false
    // 首次引导（无历史记录）同样要求用户显式确认，防止伪造的 UDP 发现诱导登录。
    return old.isBlank() || !old.equals(fresh, ignoreCase = true)
}

/**
 * 明文连接允许的目标主机：RFC1918 私网 IPv4 字面量，或本机环回
 * （localhost/127.x，无窃听面，模拟器与本地调试必需）；
 * 其余主机与所有域名一律拒绝，避免 DNS 解析把明文流量带出私网。
 */
internal fun isCleartextSafeHost(hostname: String): Boolean =
    hostname.equals("localhost", ignoreCase = true) ||
        isRfc1918Host(hostname) ||
        isLoopbackHost(hostname)

/** 仅当 hostname 是 RFC1918 私有网段（10/8、172.16/12、192.168/16）的 IPv4 字面量时返回 true。 */
internal fun isRfc1918Host(hostname: String): Boolean {
    val octets = hostname.split('.')
    if (octets.size != 4) return false
    val values = IntArray(4)
    for (i in 0..3) {
        val octet = octets[i]
        if (octet.isEmpty() || !octet.all(Char::isDigit)) return false
        val value = octet.toIntOrNull() ?: return false
        if (value > 255) return false
        values[i] = value
    }
    return values[0] == 10 ||
        (values[0] == 172 && values[1] in 16..31) ||
        (values[0] == 192 && values[1] == 168)
}

/** 环回地址段 127.0.0.0/8 的 IPv4 字面量。 */
internal fun isLoopbackHost(hostname: String): Boolean {
    val octets = hostname.split('.')
    if (octets.size != 4 || octets[0] != "127") return false
    return octets.drop(1).all { it.isNotEmpty() && it.all(Char::isDigit) && (it.toIntOrNull() ?: -1) in 0..255 }
}

/**
 * 明文（http/ws）连接只允许指向私网/环回主机，其余立即拒绝；
 * 与 networkSecurityConfig 配合，确保凭据类明文流量永远不出私网。
 */
internal fun requireCleartextPrivateUrl(url: String) {
    if (!url.startsWith("http://", ignoreCase = true) && !url.startsWith("ws://", ignoreCase = true)) return
    val host = url.substringAfter("://").substringBefore('/').substringBefore(':').substringBefore('?')
    if (!isCleartextSafeHost(host)) throw IOException("明文连接拒绝：$host 不是 RFC1918 私网地址")
}

/**
 * 用户明确填写的云服务器允许使用 HTTP 或 HTTPS；HTTP 的风险由登录界面持续提示。
 * 局域网发现和直连仍调用 [requireCleartextPrivateUrl]，不得借此放开公网 ws:// 端点。
 */
internal fun requireCloudServerUrl(url: String) {
    val parsed = runCatching { java.net.URI(url.trim()) }.getOrNull()
        ?: throw IOException("服务器地址格式无效")
    if (parsed.scheme?.lowercase() !in setOf("http", "https") || parsed.host.isNullOrBlank()) {
        throw IOException("服务器地址必须是有效的 HTTP 或 HTTPS 地址")
    }
}

/** 密码只能由云端验证，因此密码登录始终允许一次云端引导；开发者开关只控制后续连接回退。 */
internal fun planConnection(settings: WatchSettings, password: String?): ConnectionPlan = ConnectionPlan(
    bootstrapCloudAuthentication = !password.isNullOrEmpty(),
    preferLanAfterCloudAuthentication = !password.isNullOrEmpty() && settings.lanConnectionEnabled &&
        lanEndpointHosts(settings).isNotEmpty(),
    allowCloudFallback = settings.cloudConnectionEnabled,
)

internal fun schedulePullEnvelope(classId: String? = null): Envelope {
    val taskId = UUID.randomUUID().toString().replace("-", "")
    return Envelope(
        type = Protocol.TYPE_SCHEDULE_PULL,
        messageId = taskId,
        payload = protocolJson.encodeToJsonElement(
            ScheduleSyncRequest.serializer(),
            ScheduleSyncRequest(taskId = taskId, source = Protocol.SCHEDULE_SOURCE_MOBILE, classId = classId),
        ),
    )
}
