package com.remoteci.mobile.data

import android.content.Context
import android.os.Build
import com.remoteci.mobile.BuildConfig
import java.io.IOException
import java.security.MessageDigest
import java.time.Duration
import java.time.OffsetDateTime
import java.util.UUID
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.coroutines.resume
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
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

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = protocolJson
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        // 静默断网（Wi-Fi 掉线、NAT 失效）时 TCP 不会通知应用，依赖 WebSocket ping 主动探测，
        // 否则连接会“看起来还活着”却永远收不到数据、永不重连。
        .pingInterval(20, TimeUnit.SECONDS)
        .build()
    private val lanDiscoveryClient = LanDiscoveryClient(okHttp, json)
    private lateinit var sessions: SessionStorage
    // 以下字段会被 OkHttp 回调线程、IO 协程与主线程并发读写，必须保证跨线程可见性。
    @Volatile private var webSocket: WebSocket? = null
    private var activeJob: Job? = null
    private var refreshJob: Job? = null
    private var volumeJob: Job? = null
    private var discoveryJob: Job? = null
    private var schedulePullJob: Job? = null
    @Volatile private var desiredSettings: WatchSettings? = null
    @Volatile private var accessToken: String? = null
    @Volatile private var lastLanAdvertisement: PluginNetworkInfo? = null
    // OkHttp 回调线程、主线程与协程会并发读写 generation，必须用原子操作保证 attempt 校验可靠。
    private val generation = java.util.concurrent.atomic.AtomicInteger(0)
    // 断线自动重连的指数退避（连接成功后复位）；避免服务端抖动时 5 秒固定间隔造成重连风暴。
    @Volatile private var reconnectDelayMs = InitialReconnectDelayMs

    val state = MutableStateFlow<State>(State.Idle)
    /** 当前认证连接所属的 WebUI 版本；未知时禁止手表自行升级。 */
    val serverVersion = MutableStateFlow<String?>(null)
    /** 本机、服务端与当前主插件共同支持的功能；旧 V3 端缺少声明时按基础能力回退。 */
    val availableCapabilities = MutableStateFlow(Protocol.BASELINE_CAPABILITIES)
    val currentUser = MutableStateFlow<UserProfile?>(null)
    val snapshot = MutableStateFlow<ClassStateSnapshot?>(null)
    val schedule = MutableStateFlow<ScheduleBundle?>(null)
    val extensions = MutableStateFlow<List<ExtensionDefinition>>(emptyList())
    val settings = MutableStateFlow<SettingsSync?>(null)
    val events = MutableSharedFlow<ClassEvent>(extraBufferCapacity = 32)
    val lastCommandResult = MutableStateFlow<CommandResult?>(null)
    private val voiceReplies = java.util.concurrent.ConcurrentHashMap<String, kotlinx.coroutines.CompletableDeferred<CommandResult>>()

    /** 语音体积较大，在后台编码，并只接受本条消息的回执；断线时不自动重发。 */
    suspend fun sendVoiceMessage(audio: ByteArray): CommandResult = withContext(Dispatchers.IO) {
        if (currentUser.value?.has(Protocol.PERMISSION_SEND_VOICE_MESSAGES) != true)
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
    /** 网络发现或成功直连后产生的本地设置更新，由界面层持久化。 */
    val discoveredSettings = MutableSharedFlow<WatchSettings>(extraBufferCapacity = 1)
    val lanPlugins = MutableStateFlow<List<LanPluginCandidate>>(emptyList())
    val lanDiscoveryStatus = MutableStateFlow<String?>(null)
    val lanDiscoveryScanning = MutableStateFlow(false)
    /** 引导返回的云服务器地址与上次使用的不同：等待用户二次确认的候选（TOFU 强阻断）。 */
    val lanBootstrapPending = MutableStateFlow<Pair<LanPluginCandidate, WatchSettings>?>(null)

    fun initialize(context: Context) {
        if (!::sessions.isInitialized) sessions = SecureSessionStore(context.applicationContext)
    }

    /** 仅供 JVM 单元测试注入内存会话存储，绕开 Android Keystore。 */
    internal fun installSessionStorageForTest(storage: SessionStorage) {
        sessions = storage
    }

    fun hasSavedSession(): Boolean = ::sessions.isInitialized && sessions.load() != null
    fun restToken(): String? = accessToken
    fun restBaseUrl(): String? = desiredSettings?.cloudServerUrl?.trim()?.trimEnd('/')

    fun supports(capability: String): Boolean = capability in availableCapabilities.value

    fun scanLanPlugins() {
        discoveryJob?.cancel()
        lanPlugins.value = emptyList()
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

    /** password 仅用于本次 HTTPS 登录；成功后只保存 Keystore 加密的设备会话密钥。 */
    fun connect(settings: WatchSettings, password: String? = null) {
        check(::sessions.isInitialized) { "ConnectionManager 尚未初始化" }
        discoveryJob?.cancel()
        lanDiscoveryScanning.value = false
        val attempt = generation.incrementAndGet()
        desiredSettings = settings
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        webSocket?.close(1000, "switch")
        webSocket = null
        state.value = State.Connecting
        serverVersion.value = null
        lastCommandResult.value = null
        extensions.value = emptyList()
        // 重新连接后以服务端下发的设置快照为准，未同步前 UI 按默认开启处理。
        this@ConnectionManager.settings.value = null
        val plan = planConnection(settings, password)

        activeJob = scope.launch {
            try {
                if (plan.bootstrapCloudAuthentication) {
                    val auth = loginCloud(settings, password!!)
                    persist(auth)
                    // 即使随后优先走局域网 WebSocket，WebUI 管理 API 仍需要云端访问令牌。
                    // 令牌不能只在 connectCloud() 中保存，否则“局域网已连接”时所有管理页都会报未登录。
                    accessToken = auth.accessToken
                    val session = sessions.load() ?: throw MissingSessionException()
                    if (plan.preferLanAfterCloudAuthentication) {
                        // 登录端点会把新设备会话先同步给插件；镜像传播需要时间，
                        // 因此短间隔重试几次 HMAC 挑战，全部失败再回退云端中转。
                        repeat(6) {
                            delay(400)
                            if (connectLan(settings, session, attempt)) {
                                scheduleAccessRefresh(settings, auth.accessExpiresAt, attempt)
                                return@launch
                            }
                        }
                    }
                    if (!plan.allowCloudFallback)
                        throw IOException("云端认证已完成，但局域网连接失败且云端中转已关闭")
                    connectCloud(settings, auth, attempt)
                    return@launch
                }

                var saved = sessions.load() ?: throw MissingSessionException()
                if (settings.username.isNotBlank() && saved.username != settings.username)
                    throw MissingSessionException()

                // 恢复会话时先换取 WebUI 管理 API 所需的短期访问令牌，再尝试局域网直连。
                // refresh 会轮换设备密钥并同步到插件，因此直连采用刷新后的会话并短暂重试。
                val refreshed = if (plan.allowCloudFallback) refreshCloud(settings, saved) else null
                if (refreshed != null) {
                    persist(refreshed)
                    accessToken = refreshed.accessToken
                    saved = sessions.load() ?: throw MissingSessionException()
                }
                var lanOk = false
                if (settings.lanConnectionEnabled && lanEndpointHosts(settings).isNotEmpty()) {
                    repeat(if (refreshed == null) 1 else 6) { index ->
                        if (!lanOk) {
                            if (index > 0) delay(400)
                            lanOk = connectLan(settings, saved, attempt)
                        }
                    }
                }
                if (lanOk) {
                    refreshed?.let { scheduleAccessRefresh(settings, it.accessExpiresAt, attempt) }
                    return@launch
                }
                if (!plan.allowCloudFallback) throw IOException("局域网连接失败")
                connectCloud(settings, refreshed ?: throw IOException("无法刷新云端会话"), attempt)
            } catch (_: AuthenticationException) {
                state.value = State.Error("用户名或密码错误")
                serverVersion.value = null
                currentUser.value = null
            } catch (_: MissingSessionException) {
                state.value = State.Error("请先使用账号密码登录")
                serverVersion.value = null
                currentUser.value = null
            } catch (error: CancellationException) {
                // 旧连接任务被新连接取消：直接透出，不得用旧任务的取消异常
                // 覆盖新任务刚写入的 Connecting 状态或清空用户信息。
                throw error
            } catch (error: Exception) {
                // 连接过程中已写入的更具体错误（协议版本不兼容、登录已失效等）不被通用消息覆盖。
                if (state.value !is State.Error) {
                    state.value = State.Error(error.message ?: "连接失败")
                }
                serverVersion.value = null
                // 登录虽成功但连接已失败，残留的用户信息会让界面误判为“在线”。
                currentUser.value = null
            }
        }
    }

    fun disconnect(clearUser: Boolean = false) {
        generation.incrementAndGet()
        desiredSettings = null
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        schedulePullJob?.cancel()
        schedulePullState.value = SchedulePullState.Idle
        webSocket?.close(1000, "disconnect")
        webSocket = null
        serverVersion.value = null
        accessToken = null
        if (clearUser) currentUser.value = null
        extensions.value = emptyList()
        this@ConnectionManager.settings.value = null
        state.value = State.Idle
    }

    fun logout(settings: WatchSettings) {
        val token = accessToken
        val saved = sessions.load()
        if (token != null && saved != null) {
            scope.launch {
                runCatching {
                    okHttp.newCall(
                        Request.Builder()
                            .url("${settings.cloudServerUrl.trimEnd('/')}/api/auth/logout")
                            .header("Authorization", "Bearer $token")
                            .post(ByteArray(0).toRequestBody(null))
                            .build(),
                    ).execute().close()
                }
            }
        }
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
        val envelope = schedulePullEnvelope()
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
        if (currentUser.value?.canInvoke(extension) != true) {
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
        if (currentUser.value?.has(requiredPermission) != true) {
            lastCommandResult.value = CommandResult(false, "FORBIDDEN", "权限不足")
            return
        }
        lastCommandResult.value = null
        sendEnvelope(
            Envelope(
                type = Protocol.TYPE_COMMAND,
                messageId = newMessageId(),
                payload = json.encodeToJsonElement(CommandMessage.serializer(), command),
            ),
        )
    }

    private suspend fun connectLan(
        settings: WatchSettings,
        session: PersistedDeviceSession,
        attempt: Int,
    ): Boolean {
        // 明文 ws:// 直连只允许私网/环回主机，公网候选一律跳过。
        for (host in lanEndpointHosts(settings).filter(::isCleartextSafeHost)) {
            if (!connectWebSocket(
                    url = lanWebSocketUrl(host, settings.lanPort),
                    successState = State.LanConnected,
                    session = session,
                    attempt = attempt,
                )
            ) continue

            val updated = settings.copy(
                lanHost = host,
                lanHostCandidates = listOf(host) + lanEndpointHosts(settings).filterNot { it == host },
            )
            desiredSettings = updated
            if (updated != settings) discoveredSettings.tryEmit(updated)
            return true
        }
        return false
    }

    private suspend fun connectCloud(settings: WatchSettings, auth: AuthResponse, attempt: Int) {
        requireCloudServerUrl(settings.cloudServerUrl)
        accessToken = auth.accessToken
        // 服务端令牌是标准 Base64，含 +/；不编码时 + 会被服务端解码成空格导致 401。
        if (!connectWebSocket(
                cloudWebSocketUrl(settings.cloudServerUrl, auth.accessToken),
                State.CloudConnected,
                null,
                attempt,
            )
        ) throw IOException("云端连接失败")
        // 收到插件网络信息后可能已经切换到新的局域网连接尝试，旧云端协程不得再覆盖刷新任务。
        if (attempt != generation.get()) return
        scheduleAccessRefresh(settings, auth.accessExpiresAt, attempt)
    }

    private suspend fun loginCloud(settings: WatchSettings, password: String): AuthResponse =
        postAuth(
            settings,
            "/api/auth/login",
            json.encodeToString(LoginRequest(settings.username.trim(), password, "Android · ${Build.MANUFACTURER} ${Build.MODEL}")),
        )

    private suspend fun refreshCloud(settings: WatchSettings, session: PersistedDeviceSession): AuthResponse =
        try {
            postAuth(
                settings,
                "/api/auth/refresh",
                json.encodeToString(RefreshSessionRequest(session.deviceSessionId, session.deviceSecret)),
            )
        } catch (error: AuthenticationException) {
            sessions.clear()
            throw MissingSessionException()
        }

    private suspend fun postAuth(settings: WatchSettings, path: String, bodyJson: String): AuthResponse =
        withContext(Dispatchers.IO) {
            requireCloudServerUrl(settings.cloudServerUrl)
            val request = Request.Builder()
                .url("${settings.cloudServerUrl.trimEnd('/')}$path")
                .post(bodyJson.toRequestBody("application/json".toMediaType()))
                .build()
            okHttp.newCall(request).execute().use { response ->
                if (response.code == 401) throw AuthenticationException()
                if (!response.isSuccessful) throw IOException("登录服务返回 HTTP ${response.code}")
                json.decodeFromString(AuthResponse.serializer(), response.body.string())
            }
        }

    private fun persist(auth: AuthResponse) {
        sessions.save(
            PersistedDeviceSession(
                username = auth.user.username,
                deviceSessionId = auth.deviceSessionId,
                deviceSecret = auth.deviceSecret,
                deviceExpiresAt = auth.deviceExpiresAt,
            ),
        )
        currentUser.value = auth.user
    }

    private suspend fun connectWebSocket(
        url: String,
        successState: State,
        session: PersistedDeviceSession?,
        attempt: Int,
    ): Boolean = try {
        // 认证阶段限时：故障或恶意对端完成握手后从不回 auth_state 时（readTimeout=0 + ping 保活
        // 会让死连接永久存活），超时后按连接失败走外层回退，而不是永久停在“连接中”。
        withTimeout(AuthHandshakeTimeoutMs) {
            awaitAuthenticatedSocket(url, successState, session, attempt)
        }
    } catch (_: TimeoutCancellationException) {
        false
    }

    private suspend fun awaitAuthenticatedSocket(
        url: String,
        successState: State,
        session: PersistedDeviceSession?,
        attempt: Int,
    ): Boolean = suspendCancellableCoroutine { continuation ->
        // 只有“认证成功进入工作状态”的连接在断开后才允许自动重连；
        // 连接失败或认证被拒的 socket 由外层流程决定回退，否则每次失败都会调度一次全量重连形成风暴。
        var authenticated = false
        val listener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                // 超时或已切换到新尝试：不得让过期 socket 占据活跃连接字段。
                if (attempt != generation.get() || !continuation.isActive) webSocket.close(1000, "superseded")
                else ConnectionManager.webSocket = webSocket
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (attempt != generation.get()) {
                    webSocket.close(1000, "superseded")
                    if (continuation.isActive) continuation.resume(false)
                    return
                }
                val envelope = runCatching { json.decodeFromString(Envelope.serializer(), text) }.getOrNull() ?: return
                if (envelope.protocolVersion != Protocol.VERSION) {
                    state.value = State.Error("协议版本不兼容，需要 v${Protocol.VERSION}")
                    webSocket.close(1008, "protocol")
                    if (continuation.isActive) continuation.resume(false)
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

                val auth = handleEnvelope(envelope)
                if (auth != null && continuation.isActive) {
                    if (auth.authenticated && auth.user != null) {
                        sendCapabilitiesReport(webSocket)
                        state.value = successState
                        authenticated = true
                        reconnectDelayMs = InitialReconnectDelayMs // 连接成功即复位退避。
                        continuation.resume(true)
                    } else {
                        webSocket.close(1008, "auth failed")
                        continuation.resume(false)
                    }
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (continuation.isActive) continuation.resume(false)
                clearActiveConnection(webSocket)
                // OkHttp 中 onFailure 与 onClosed 互斥：异常断开（Wi-Fi 掉线、NAT 失效等）
                // 只会触发 onFailure，必须在此调度自动重连，否则断线后永远无法恢复。
                if (authenticated && attempt == generation.get()) {
                    state.value = State.Error("连接已断开：${t.message ?: "网络不可用"}")
                    scheduleReconnect(attempt)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                clearActiveConnection(webSocket)
                if (authenticated && attempt == generation.get() && !continuation.isActive) scheduleReconnect(attempt)
            }
        }
        val socket = okHttp.newWebSocket(Request.Builder().url(url).build(), listener)
        continuation.invokeOnCancellation { socket.close(1000, "cancelled") }
    }

    /** 只清理由当前活跃连接持有的版本，避免旧连接的迟到回调清空新连接状态。 */
    private fun clearActiveConnection(socket: WebSocket) {
        if (webSocket !== socket) return
        webSocket = null
        serverVersion.value = null
        availableCapabilities.value = Protocol.BASELINE_CAPABILITIES
    }

    private fun handleEnvelope(envelope: Envelope): AuthState? = when (envelope.type) {
        Protocol.TYPE_AUTH_STATE -> decodePayload(envelope.payload, AuthState.serializer())?.also { auth ->
            currentUser.value = if (auth.authenticated) auth.user else null
            serverVersion.value = if (auth.authenticated) auth.serverVersion else null
            if (!auth.authenticated) state.value = State.Error(auth.error ?: "登录已失效")
        }
        Protocol.TYPE_STATE_PUSH -> {
            decodePayload(envelope.payload, ClassStateSnapshot.serializer())?.let { snapshot.value = it }
            null
        }
        Protocol.TYPE_SCHEDULE_SYNC -> {
            decodePayload(envelope.payload, ScheduleBundle.serializer())?.let {
                schedule.value = it
                // 兼容未实现状态消息的旧插件：收到新课表本身也可作为成功终态。
                if (schedulePullState.value is SchedulePullState.Pulling)
                    finishSchedulePull(SchedulePullState.Success("课表拉取完成，已使用插件最新课表"))
            }
            null
        }
        Protocol.TYPE_SCHEDULE_SYNC_STATUS -> {
            decodePayload(envelope.payload, ScheduleSyncStatus.serializer())?.let(::applyScheduleSyncStatus)
            null
        }
        Protocol.TYPE_EXTENSIONS_SYNC -> {
            decodePayload(envelope.payload, ListSerializer(ExtensionDefinition.serializer()))
                ?.let { extensions.value = it }
            null
        }
        Protocol.TYPE_SETTINGS_SYNC -> {
            decodePayload(envelope.payload, SettingsSync.serializer())?.let { settings.value = it }
            null
        }
        Protocol.TYPE_PLUGIN_NETWORK_INFO -> {
            decodePayload(envelope.payload, PluginNetworkInfo.serializer())?.let { handlePluginNetworkInfo(it) }
            null
        }
        Protocol.TYPE_CAPABILITIES_SYNC -> {
            decodePayload(envelope.payload, CapabilitiesSync.serializer())?.let { sync ->
                availableCapabilities.value = effectiveCapabilities(sync)
            }
            null
        }
        Protocol.TYPE_EVENT_NOTIFY -> {
            decodePayload(envelope.payload, ClassEvent.serializer())?.let { events.tryEmit(it) }
            null
        }
        Protocol.TYPE_COMMAND_RESULT -> {
            decodePayload(envelope.payload, CommandResult.serializer())?.let {
                lastCommandResult.value = it
                envelope.replyToMessageId?.let { id -> voiceReplies[id]?.complete(it) }
            }
            null
        }
        else -> null
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
        val current = desiredSettings ?: return
        val updated = mergePluginNetworkInfo(current, info)
        desiredSettings = updated
        if (updated != current) discoveredSettings.tryEmit(updated)

        // 同一份不可达地址回退到云端后不反复重试；网卡或端口变化时才重新优先直连。
        val isNewAdvertisement = info != lastLanAdvertisement
        lastLanAdvertisement = info
        if (isNewAdvertisement && info.lanServerEnabled && updated.lanConnectionEnabled &&
            lanEndpointHosts(updated).isNotEmpty() && state.value == State.CloudConnected
        ) {
            connect(updated)
        }
    }

    private fun scheduleAccessRefresh(settings: WatchSettings, expiresAt: String, attempt: Int) {
        refreshJob?.cancel()
        val delayMillis = runCatching {
            (Duration.between(OffsetDateTime.now(), OffsetDateTime.parse(expiresAt)).toMillis() - 60_000)
                .coerceAtLeast(30_000)
        }.getOrDefault(50 * 60_000L)
        refreshJob = scope.launch {
            delay(delayMillis)
            if (attempt == generation.get()) connect(settings)
        }
    }

    private fun scheduleReconnect(attempt: Int) {
        val settings = desiredSettings ?: return
        val delayMs = reconnectDelayMs
        reconnectDelayMs = nextReconnectDelay(reconnectDelayMs)
        // 退避期间保留断线原因展示，connect() 真正发起时才切换到 Connecting。
        scope.launch {
            delay(delayMs)
            if (attempt == generation.get()) connect(settings)
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

internal fun effectiveCapabilities(sync: CapabilitiesSync): Set<String> =
    Protocol.CURRENT_CAPABILITIES
        .intersect(sync.server.capabilities.toSet())
        .intersect(sync.plugin?.capabilities?.toSet() ?: emptySet())

internal data class ConnectionPlan(
    val bootstrapCloudAuthentication: Boolean,
    val preferLanAfterCloudAuthentication: Boolean,
    val allowCloudFallback: Boolean,
)

internal const val InitialReconnectDelayMs = 5_000L
internal const val MaxReconnectDelayMs = 60_000L

/** 认证握手限时：对端只完成 WebSocket 握手但从不回 auth_state 时不得永久挂起。 */
internal const val AuthHandshakeTimeoutMs = 15_000L

/** 手动拉取课表等待插件回传的最长时间，与 WebUI 的等待上限保持一致。 */
internal const val SchedulePullTimeoutMs = 15_000L

/** 断线重连的指数退避：每次翻倍并封顶 [MaxReconnectDelayMs]；连接成功后调用方复位为 [InitialReconnectDelayMs]。 */
internal fun nextReconnectDelay(currentMs: Long): Long = (currentMs * 2).coerceAtMost(MaxReconnectDelayMs)

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
    val schemeUrl = cloudServerUrl.trimEnd('/')
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

internal fun schedulePullEnvelope(): Envelope {
    val taskId = UUID.randomUUID().toString().replace("-", "")
    return Envelope(
        type = Protocol.TYPE_SCHEDULE_PULL,
        messageId = taskId,
        payload = protocolJson.encodeToJsonElement(
            ScheduleSyncRequest.serializer(),
            ScheduleSyncRequest(taskId = taskId, source = Protocol.SCHEDULE_SOURCE_MOBILE),
        ),
    )
}
