package com.remoteci.mobile.data

import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.encodeToString
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * ConnectionManager 完整连接流程测试：真实 OkHttp + MockWebServer 端到端，
 * 通过 SessionStorage 内存假实现绕开 Android Keystore，纯 JVM 运行。
 * 覆盖：密码登录、错误密码、局域网 HMAC 挑战握手、协议版本不兼容、命令回环、断线自动重连、
 * 令牌复用与失效换新、失败自动重试、并发刷新串行化，以及多班级数据归属。
 */
class ConnectionManagerFlowTest {
    private lateinit var server: MockWebServer
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    private companion object {
        const val SESSION_ID = "ABCDEF12-3456-7890-ABCD-EF1234567890"
        const val DEVICE_SECRET = "secret-value"
        val TEACHER = UserProfile(
            id = "u1",
            username = "teacher",
            displayName = "王老师",
            role = Protocol.ROLE_ADMIN,
            permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SEND_NOTIFICATIONS or
                Protocol.PERMISSION_MANAGE_SCHEDULE,
        )
        const val CLASS_A = "11111111-1111-1111-1111-111111111111"
        const val CLASS_B = "22222222-2222-2222-2222-222222222222"
        val MULTI_CLASS_TEACHER = TEACHER.copy(
            classes = listOf(ClassSummary(id = CLASS_A, name = "一班"), ClassSummary(id = CLASS_B, name = "二班")),
        )
    }

    private class FakeSessionStorage(initial: PersistedDeviceSession? = null) : SessionStorage {
        var stored: PersistedDeviceSession? = initial
        override fun save(session: PersistedDeviceSession) {
            stored = session
        }

        override fun load(): PersistedDeviceSession? = stored
        override fun clear() {
            stored = null
        }
    }

    @BeforeTest
    fun setUp() {
        server = MockWebServer()
        server.start()
    }

    @AfterTest
    fun tearDown() {
        ConnectionManager.disconnect(clearUser = true)
        server.close()
    }

    private fun authResponse(
        accessToken: String = "tok-abc",
        deviceSecret: String = DEVICE_SECRET,
        user: UserProfile = TEACHER,
    ) = AuthResponse(
        accessToken = accessToken,
        accessExpiresAt = "2099-01-01T00:00:00Z",
        deviceSessionId = SESSION_ID,
        deviceSecret = deviceSecret,
        deviceExpiresAt = "2099-01-01T00:00:00Z",
        user = user,
    )

    private fun authBody(response: AuthResponse = authResponse()) =
        MockResponse.Builder().code(200).body(json.encodeToString(AuthResponse.serializer(), response)).build()

    private fun session(username: String = "teacher") = PersistedDeviceSession(
        username = username,
        deviceSessionId = SESSION_ID,
        deviceSecret = DEVICE_SECRET,
        deviceExpiresAt = "2099-01-01T00:00:00Z",
    )

    private fun envelopeJson(type: String, protocolVersion: Int = Protocol.VERSION, payload: JsonElement? = null) =
        json.encodeToString(
            Envelope.serializer(),
            Envelope(protocolVersion = protocolVersion, type = type, payload = payload),
        )

    private fun authStateJson(user: UserProfile? = TEACHER) = envelopeJson(
        Protocol.TYPE_AUTH_STATE,
        payload = json.encodeToJsonElement(
            AuthState.serializer(),
            AuthState(authenticated = true, serverVersion = "0.4.0", user = user),
        ),
    )

    private fun scheduleJson(classId: String) = envelopeJson(
        Protocol.TYPE_SCHEDULE_SYNC,
        payload = json.encodeToJsonElement(
            ScheduleBundle.serializer(),
            ScheduleBundle(classId = classId, fromDate = "2026-10-05"),
        ),
    )

    /** 局域网插件：先发挑战，收到证明后下发认证状态（插件镜像中的账号不带班级列表）。 */
    private fun lanPluginListener(
        user: UserProfile = TEACHER.copy(classes = null),
        afterAuth: (WebSocket) -> Unit = {},
    ) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            webSocket.send(
                envelopeJson(
                    Protocol.TYPE_AUTH_CHALLENGE,
                    payload = json.encodeToJsonElement(
                        AuthChallenge.serializer(),
                        AuthChallenge(challengeId = "c1", nonce = "n1", expiresAt = "2099-01-01T00:00:00Z"),
                    ),
                ),
            )
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (json.decodeFromString(Envelope.serializer(), text).type == Protocol.TYPE_AUTH_PROOF) {
                webSocket.send(authStateJson(user))
                afterAuth(webSocket)
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }
    }

    private fun drainRequestLines(): List<String> = buildList {
        while (true) {
            val request = server.takeRequest(500, TimeUnit.MILLISECONDS) ?: break
            add(request.requestLine)
        }
    }

    /** 打开即下发认证成功状态并保持连接（不主动关闭，由测试断开触发重连）。 */
    private fun authStateListener() = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            webSocket.send(authStateJson())
        }

        // 必须回显关闭帧，客户端与服务端才能完成关闭握手，MockWebServer 才能干净停机。
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }
    }

    private fun mockServerSettings() = WatchSettings(
        cloudServerUrl = server.url("/").toString().trimEnd('/'),
        lanConnectionEnabled = false,
    )

    private suspend fun awaitState(
        predicate: (ConnectionManager.State) -> Boolean,
        timeoutMs: Long = 15_000,
    ): ConnectionManager.State = withTimeout(timeoutMs) { ConnectionManager.state.first(predicate) }

    @Test
    fun `password login persists device session and reaches cloud connected`() = runBlocking {
        val storage = FakeSessionStorage()
        ConnectionManager.installSessionStorageForTest(storage)
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings(), "correct-password")

        awaitState({ it is ConnectionManager.State.CloudConnected })
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
        assertEquals("0.4.0", ConnectionManager.serverVersion.value)
        assertEquals(SESSION_ID, storage.stored?.deviceSessionId)

        val loginRequest = server.takeRequest()
        assertTrue(loginRequest.requestLine.startsWith("POST /api/auth/login "))
        val wsRequest = server.takeRequest()
        assertTrue(wsRequest.requestLine.contains("/ws?token=tok-abc"))
    }

    @Test
    fun `wrong password surfaces clear authentication error`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(MockResponse.Builder().code(401).body("unauthorized").build())

        ConnectionManager.connect(mockServerSettings(), "wrong-password")

        val error = awaitState({ it is ConnectionManager.State.Error })
        assertEquals("用户名或密码错误", (error as ConnectionManager.State.Error).message)
        assertNull(ConnectionManager.currentUser.value)
    }

    @Test
    fun `lan challenge proof verifies server-side and reaches lan connected`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage(session()))
        val challenge = AuthChallenge(challengeId = "c1", nonce = "n1", expiresAt = "2099-01-01T00:00:00Z")
        var proofVerified = false
        val wsListener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send(
                    envelopeJson(
                        Protocol.TYPE_AUTH_CHALLENGE,
                        payload = json.encodeToJsonElement(AuthChallenge.serializer(), challenge),
                    ),
                )
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val envelope = json.decodeFromString(Envelope.serializer(), text)
                if (envelope.type == Protocol.TYPE_PEER_CAPABILITIES) {
                    assertTrue(proofVerified)
                    return
                }
                assertEquals(Protocol.TYPE_AUTH_PROOF, envelope.type)
                val proof = json.decodeFromJsonElement(AuthProof.serializer(), envelope.payload!!)
                assertEquals("c1", proof.challengeId)
                assertEquals(SESSION_ID, proof.deviceSessionId)
                // 服务端独立重算 HMAC：密钥 = SHA-256(deviceSecret)，消息 = 3|c1|n1|clientNonce|无横线小写 sessionId
                val verifier = MessageDigest.getInstance("SHA-256").digest(DEVICE_SECRET.encodeToByteArray())
                val canonical = "3|c1|n1|${proof.clientNonce}|${SESSION_ID.replace("-", "").lowercase()}"
                val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(verifier, "HmacSHA256")) }
                assertEquals(
                    Base64.getEncoder().encodeToString(mac.doFinal(canonical.encodeToByteArray())),
                    proof.proof,
                )
                proofVerified = true
                webSocket.send(authStateJson())
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }
        }
        server.enqueue(MockResponse.Builder().webSocketUpgrade(wsListener).build())
        // 已保存会话的局域网连接先于管理 API 令牌刷新；刷新请求在 WebSocket 成功后后台发送。
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        val settings = WatchSettings(
            cloudServerUrl = server.url("/").toString().trimEnd('/'),
            lanConnectionEnabled = true,
            lanHost = "localhost",
            lanPort = server.port,
        )

        ConnectionManager.connect(settings)

        awaitState({ it is ConnectionManager.State.LanConnected })
        assertTrue(proofVerified)
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
        // 局域网连接不再等待云端往返；管理 API 令牌在连接成功后后台刷新。
        withTimeout(5_000) {
            while (ConnectionManager.restToken() == null) delay(10)
        }
        assertEquals("tok-abc", ConnectionManager.restToken())
    }

    @Test
    fun `protocol version mismatch keeps specific error message`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(envelopeJson(Protocol.TYPE_AUTH_STATE, protocolVersion = 99))
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )

        ConnectionManager.connect(mockServerSettings(), "correct-password")

        val error = awaitState({ it is ConnectionManager.State.Error })
        assertEquals("协议版本不兼容，需要 v3", (error as ConnectionManager.State.Error).message)
    }

    @Test
    fun `command round trip updates last command result`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(authStateJson())
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val envelope = json.decodeFromString(Envelope.serializer(), text)
                    if (envelope.type == Protocol.TYPE_COMMAND) {
                        webSocket.send(
                            envelopeJson(
                                Protocol.TYPE_COMMAND_RESULT,
                                payload = json.encodeToJsonElement(
                                    CommandResult.serializer(),
                                    CommandResult(success = true, code = "OK", message = "已发送"),
                                ),
                            ),
                        )
                    }
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })

        ConnectionManager.sendNotification("标题", "内容", false, false, false)

        withTimeout(10_000) { ConnectionManager.lastCommandResult.first { it != null } }
        assertEquals("OK", ConnectionManager.lastCommandResult.value?.code)
        assertTrue(ConnectionManager.lastCommandResult.value?.success == true)
    }

    @Test
    fun `schedule change sends command with class and request payload`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(authBody(authResponse(user = TEACHER.copy(
            classes = listOf(
                ClassSummary(
                    id = CLASS_A,
                    name = "一班",
                    permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_SCHEDULE,
                ),
            ),
        ))))
        val received = kotlinx.coroutines.CompletableDeferred<CommandMessage>()
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(authStateJson(TEACHER.copy(
                        classes = listOf(
                            ClassSummary(
                                id = CLASS_A,
                                name = "一班",
                                permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_SCHEDULE,
                            ),
                        ),
                    )))
                    webSocket.send(
                        envelopeJson(
                            Protocol.TYPE_CAPABILITIES_SYNC,
                            payload = json.encodeToJsonElement(
                                CapabilitiesSync.serializer(),
                                CapabilitiesSync(
                                    server = PeerCapabilities(capabilities = Protocol.CURRENT_CAPABILITIES.toList()),
                                    classPlugins = listOf(
                                        ClassPluginCapabilities(
                                            CLASS_A,
                                            PeerCapabilities(capabilities = Protocol.CURRENT_CAPABILITIES.toList()),
                                        ),
                                    ),
                                ),
                            ),
                        ),
                    )
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val envelope = json.decodeFromString(Envelope.serializer(), text)
                    if (envelope.type == Protocol.TYPE_COMMAND) {
                        val command = json.decodeFromJsonElement(CommandMessage.serializer(), envelope.payload!!)
                        received.complete(command)
                        webSocket.send(
                            envelopeJson(
                                Protocol.TYPE_COMMAND_RESULT,
                                payload = json.encodeToJsonElement(
                                    CommandResult.serializer(),
                                    CommandResult(success = true, code = "OK", message = "已完成"),
                                ),
                            ),
                        )
                    }
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })
        ConnectionManager.sendScheduleChange(
            ScheduleChangeRequest(
                date = "2026-10-05",
                mode = Protocol.CHANGE_EXCHANGE,
                sourceIndex = 1,
                targetIndex = 3,
                expectedRevision = "rev-1",
                permanent = false,
            ),
        )

        val command = withTimeout(5_000) { received.await() }
        assertEquals(Protocol.CMD_CHANGE_SCHEDULE, command.command)
        assertEquals(CLASS_A, command.classId)
        assertEquals("2026-10-05", command.scheduleChange?.date)
        assertEquals(Protocol.CHANGE_EXCHANGE, command.scheduleChange?.mode)
        assertEquals(1, command.scheduleChange?.sourceIndex)
        assertEquals(3, command.scheduleChange?.targetIndex)
        assertEquals("rev-1", command.scheduleChange?.expectedRevision)
    }

    @Test
    fun `authenticated connection drop reconnects reusing the still valid token`() = runBlocking {
        val storage = FakeSessionStorage()
        ConnectionManager.installSessionStorageForTest(storage)
        server.enqueue(authBody())
        var firstSocket: WebSocket? = null
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    firstSocket = webSocket
                    webSocket.send(authStateJson())
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )
        // 访问令牌仍有余量：断线重连直接升级第二条 WebSocket，不再刷新（刷新会轮换设备密钥并触发认证限流）。
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })

        firstSocket!!.close(1000, "network dropped")

        // 断开后不能停留在“已连接”：先进入带自动重连提示的错误状态，再等待 5 秒退避后的重连完成。
        val dropped = awaitState({ it !is ConnectionManager.State.CloudConnected }, timeoutMs = 10_000)
        assertTrue((dropped as ConnectionManager.State.Error).message.contains("自动重连"), dropped.message)
        awaitState({ it is ConnectionManager.State.CloudConnected }, timeoutMs = 25_000)

        val requestLines = drainRequestLines()
        assertTrue(requestLines.none { it.startsWith("POST /api/auth/refresh ") }, "unexpected refresh: $requestLines")
        assertEquals(2, requestLines.count { it.contains("/ws?token=tok-abc") }, "$requestLines")
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
    }

    @Test
    fun `rejected websocket token is refreshed once and the rotated session persisted`() = runBlocking {
        val storage = FakeSessionStorage()
        ConnectionManager.installSessionStorageForTest(storage)
        server.enqueue(authBody())
        var firstSocket: WebSocket? = null
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    firstSocket = webSocket
                    webSocket.send(authStateJson())
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )
        // 重连时缓存令牌已被服务端作废：升级返回 401，客户端应强制刷新并用新令牌重试。
        server.enqueue(MockResponse.Builder().code(401).body("unauthorized").build())
        server.enqueue(authBody(authResponse(accessToken = "tok-new", deviceSecret = "secret-rotated")))
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })
        firstSocket!!.close(1000, "token revoked")
        awaitState({ it !is ConnectionManager.State.CloudConnected }, timeoutMs = 10_000)
        awaitState({ it is ConnectionManager.State.CloudConnected }, timeoutMs = 25_000)

        val requestLines = drainRequestLines()
        assertEquals(1, requestLines.count { it.startsWith("POST /api/auth/refresh ") }, "$requestLines")
        assertTrue(requestLines.last().contains("/ws?token=tok-new"), "$requestLines")
        assertEquals("secret-rotated", storage.stored?.deviceSecret)
        assertEquals("tok-new", ConnectionManager.restToken())
    }

    @Test
    fun `failed connection attempt retries automatically`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage(session()))
        server.enqueue(authBody())
        // 第一次升级遇到服务器暂时不可用；以前会永久停在错误状态，直到用户重开应用。
        server.enqueue(MockResponse.Builder().code(503).body("unavailable").build())
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings())

        val error = awaitState({ it is ConnectionManager.State.Error })
        assertTrue((error as ConnectionManager.State.Error).message.contains("自动重试"), error.message)
        awaitState({ it is ConnectionManager.State.CloudConnected }, timeoutMs = 25_000)
        // 重试复用第一次刷新得到的令牌，不再重复轮换设备密钥。
        assertEquals(1, drainRequestLines().count { it.startsWith("POST /api/auth/refresh ") })
    }

    @Test
    fun `back to back connects share one refresh and keep the rotated secret`() = runBlocking {
        val storage = FakeSessionStorage(session())
        ConnectionManager.installSessionStorageForTest(storage)
        // 模拟真实服务端：刷新会轮换设备密钥，旧密钥立即失效。
        val lock = Any()
        val firstRefreshArrived = kotlinx.coroutines.CompletableDeferred<Unit>()
        var currentSecret = DEVICE_SECRET
        var rotations = 0
        var rejected = 0
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.requestLine.startsWith("POST /api/auth/refresh ")) {
                    firstRefreshArrived.complete(Unit)
                    val body = json.decodeFromString(RefreshSessionRequest.serializer(), request.body!!.utf8())
                    val issuedSecret = synchronized(lock) {
                        if (body.deviceSecret != currentSecret) {
                            rejected++
                            null
                        } else {
                            rotations++
                            currentSecret = "secret-$rotations"
                            currentSecret
                        }
                    } ?: return MockResponse.Builder().code(401).body("unauthorized").build()
                    return MockResponse.Builder().code(200).headersDelay(300, TimeUnit.MILLISECONDS)
                        .body(json.encodeToString(AuthResponse.serializer(), authResponse(deviceSecret = issuedSecret)))
                        .build()
                }
                return MockResponse.Builder().webSocketUpgrade(authStateListener()).build()
            }
        }

        // 第一次刷新已发到服务端时又触发一次连接（回到前台、网络切换等）：
        // 后一次会取消前一次，但前一次的刷新已在服务端轮换了密钥，结果必须落盘，后一次不得拿旧密钥再刷新。
        ConnectionManager.connect(mockServerSettings())
        withTimeout(5_000) { firstRefreshArrived.await() }
        ConnectionManager.connect(mockServerSettings())

        awaitState({ it is ConnectionManager.State.CloudConnected })
        synchronized(lock) {
            assertEquals(1, rotations)
            assertEquals(0, rejected)
            assertEquals(currentSecret, storage.stored?.deviceSecret)
        }
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
    }

    @Test
    fun `switching class shows the cached schedule of that class without pulling`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(authBody(authResponse(user = MULTI_CLASS_TEACHER)))
        val received = java.util.concurrent.CopyOnWriteArrayList<String>()
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    // 服务端在认证后为每个可访问班级各推一份课表。
                    webSocket.send(authStateJson(MULTI_CLASS_TEACHER))
                    webSocket.send(scheduleJson(CLASS_A))
                    webSocket.send(scheduleJson(CLASS_B))
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    received += json.decodeFromString(Envelope.serializer(), text).type
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })
        withTimeout(5_000) { ConnectionManager.schedule.first { it?.classId == CLASS_A } }
        delay(300) // 等待二班课表也已到达并被缓存。

        ConnectionManager.switchClass(CLASS_B)

        assertEquals(CLASS_B, ConnectionManager.currentClassId.value)
        assertEquals(CLASS_B, ConnectionManager.schedule.value?.classId)
        delay(300)
        assertFalse(Protocol.TYPE_SCHEDULE_PULL in received, "$received")
        assertTrue(ConnectionManager.state.value is ConnectionManager.State.CloudConnected)
    }

    @Test
    fun `plugin network info of another class does not trigger a lan reconnect`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(authBody(authResponse(user = MULTI_CLASS_TEACHER)))
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(authStateJson(MULTI_CLASS_TEACHER))
                    webSocket.send(
                        envelopeJson(
                            Protocol.TYPE_PLUGIN_NETWORK_INFO,
                            payload = json.encodeToJsonElement(
                                PluginNetworkInfo.serializer(),
                                PluginNetworkInfo(true, listOf("192.168.77.10"), 8765, classId = CLASS_B),
                            ),
                        ),
                    )
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )

        ConnectionManager.connect(mockServerSettings().copy(lanConnectionEnabled = true), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })
        assertEquals(CLASS_A, ConnectionManager.currentClassId.value)

        // 二班插件的地址不能成为一班的直连候选，也不能打断现有云端连接。
        delay(1_500)
        assertTrue(ConnectionManager.state.value is ConnectionManager.State.CloudConnected)
        assertEquals(2, server.requestCount)
    }

    @Test
    fun `lan auth state without classes keeps the class list from cloud login`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        server.enqueue(authBody(authResponse(user = MULTI_CLASS_TEACHER)))
        server.enqueue(MockResponse.Builder().webSocketUpgrade(lanPluginListener()).build())
        val settings = WatchSettings(
            cloudServerUrl = server.url("/").toString().trimEnd('/'),
            lanConnectionEnabled = true,
            lanHost = "localhost",
            lanPort = server.port,
        )

        ConnectionManager.connect(settings, "correct-password")

        awaitState({ it is ConnectionManager.State.LanConnected })
        assertEquals(listOf(CLASS_A, CLASS_B), ConnectionManager.classes.value.map { it.id })
        assertEquals(CLASS_A, ConnectionManager.currentClassId.value)
        // 管理接口令牌直接复用登录签发的令牌，不额外刷新。
        assertEquals("tok-abc", ConnectionManager.restToken())
    }

    @Test
    fun `silent lan candidate does not delay a reachable one`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage(session()))
        // 127.0.0.0/8 均为环回：插件监听 127.0.0.1，127.0.0.2 同端口上的“虚拟网卡”只接受 TCP、永不应答。
        val plugin = MockWebServer()
        plugin.start(java.net.InetAddress.getByName("127.0.0.1"), 0)
        val silent = java.net.ServerSocket(plugin.port, 50, java.net.InetAddress.getByName("127.0.0.2"))
        val accepted = java.util.concurrent.CopyOnWriteArrayList<java.net.Socket>()
        val acceptor = Thread {
            runCatching { while (true) accepted += silent.accept() }
        }.apply { isDaemon = true; start() }
        try {
            plugin.enqueue(MockResponse.Builder().webSocketUpgrade(lanPluginListener()).build())
            val settings = WatchSettings(
                cloudServerUrl = server.url("/").toString().trimEnd('/'),
                cloudConnectionEnabled = false,
                lanConnectionEnabled = true,
                lanHost = "127.0.0.2",
                lanHostCandidates = listOf("127.0.0.1"),
                lanPort = plugin.port,
            )

            val started = System.nanoTime()
            ConnectionManager.connect(settings)
            awaitState({ it is ConnectionManager.State.LanConnected }, timeoutMs = 10_000)

            // 逐个探测时要先等首选地址 4 秒握手超时；并发探测应几乎立即连上可达地址。
            val elapsedMs = (System.nanoTime() - started) / 1_000_000
            assertTrue(elapsedMs < 2_000, "lan connect took ${elapsedMs}ms")
        } finally {
            ConnectionManager.disconnect(clearUser = true)
            silent.close()
            acceptor.join(1_000)
            accepted.forEach { runCatching { it.close() } }
            plugin.close()
        }
    }

    @Test
    fun `lan schedule received before the class is known survives the profile refresh`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage(session()))
        // 插件局域网推送的课表不带班级标识，且早于后台刷新得到的班级列表到达。
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(
                lanPluginListener { socket ->
                    socket.send(
                        envelopeJson(
                            Protocol.TYPE_SCHEDULE_SYNC,
                            payload = json.encodeToJsonElement(ScheduleBundle.serializer(), ScheduleBundle(fromDate = "2026-10-05")),
                        ),
                    )
                },
            ).build(),
        )
        server.enqueue(
            authBody(authResponse(user = TEACHER.copy(classes = listOf(ClassSummary(id = CLASS_A, name = "一班"))))),
        )
        val settings = WatchSettings(
            cloudServerUrl = server.url("/").toString().trimEnd('/'),
            lanConnectionEnabled = true,
            lanHost = "localhost",
            lanPort = server.port,
        )

        ConnectionManager.connect(settings)

        awaitState({ it is ConnectionManager.State.LanConnected })
        withTimeout(5_000) {
            ConnectionManager.schedule.first { it != null }
            while (ConnectionManager.restToken() == null) delay(10)
        }
        delay(200)
        // 刷新确定班级后不能把已展示的课表清空（以前要等插件下次推送课表才恢复）。
        assertEquals(CLASS_A, ConnectionManager.currentClassId.value)
        assertEquals("2026-10-05", ConnectionManager.schedule.value?.fromDate)
    }

    @Test
    fun `parallel lan probes keep a single active connection`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage(session()))
        // 两个候选地址指向同一插件：两条探测都可能认证成功，只能保留先到的一条，落选者关闭时不得触发重连。
        server.enqueue(MockResponse.Builder().webSocketUpgrade(lanPluginListener()).build())
        server.enqueue(MockResponse.Builder().webSocketUpgrade(lanPluginListener()).build())
        val settings = WatchSettings(
            cloudServerUrl = server.url("/").toString().trimEnd('/'),
            cloudConnectionEnabled = false,
            lanConnectionEnabled = true,
            lanHost = "localhost",
            lanHostCandidates = listOf("127.0.0.1"),
            lanPort = server.port,
        )

        ConnectionManager.connect(settings)

        awaitState({ it is ConnectionManager.State.LanConnected })
        delay(1_500)
        assertTrue(ConnectionManager.state.value is ConnectionManager.State.LanConnected)
        assertTrue(server.requestCount <= 2, "unexpected reconnect: ${server.requestCount} requests")
    }

    @Test
    fun `superseded connect cancellation does not corrupt the new attempt state`() = runBlocking {
        ConnectionManager.installSessionStorageForTest(FakeSessionStorage())
        // 第一次连接：登录成功但升级后对端从不回 auth_state，协程挂起在认证等待中。
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, reason)
                }
            }).build(),
        )
        // 第二次连接：正常登录并下发 auth_state。
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        // 等待旧尝试已发出登录与升级请求、挂起等待 auth_state。
        server.takeRequest()
        server.takeRequest()

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })

        // 旧任务被取消后必须直接透出 CancellationException：
        // 不得把新连接的已连接状态覆盖为 Error，也不得清空用户信息。
        delay(500)
        assertTrue(ConnectionManager.state.value is ConnectionManager.State.CloudConnected)
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
    }
}
