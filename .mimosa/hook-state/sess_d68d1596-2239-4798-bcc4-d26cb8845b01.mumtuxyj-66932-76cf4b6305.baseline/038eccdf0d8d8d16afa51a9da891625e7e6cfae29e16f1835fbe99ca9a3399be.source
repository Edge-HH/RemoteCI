package com.remoteci.watch.data

import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
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
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * ConnectionManager 完整连接流程测试：真实 OkHttp + MockWebServer 端到端，
 * 通过 SessionStorage 内存假实现绕开 Android Keystore，纯 JVM 运行。
 * 覆盖：密码登录、错误密码、局域网 HMAC 挑战握手、协议版本不兼容、命令回环与断线自动重连。
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

    private fun authResponse() = AuthResponse(
        accessToken = "tok-abc",
        accessExpiresAt = "2099-01-01T00:00:00Z",
        deviceSessionId = SESSION_ID,
        deviceSecret = DEVICE_SECRET,
        deviceExpiresAt = "2099-01-01T00:00:00Z",
        user = TEACHER,
    )

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

    private fun authStateJson() = envelopeJson(
        Protocol.TYPE_AUTH_STATE,
        payload = json.encodeToJsonElement(
            AuthState.serializer(),
            AuthState(authenticated = true, serverVersion = "0.4.0", user = TEACHER),
        ),
    )

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
    fun `authenticated connection drop schedules reconnect via refresh`() = runBlocking {
        val storage = FakeSessionStorage()
        ConnectionManager.installSessionStorageForTest(storage)
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
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
        // 断线后的自动重连：先刷新会话，再升级第二条 WebSocket。
        server.enqueue(
            MockResponse.Builder().code(200)
                .body(json.encodeToString(AuthResponse.serializer(), authResponse())).build(),
        )
        server.enqueue(MockResponse.Builder().webSocketUpgrade(authStateListener()).build())

        ConnectionManager.connect(mockServerSettings(), "correct-password")
        awaitState({ it is ConnectionManager.State.CloudConnected })

        firstSocket!!.close(1000, "network dropped")

        // 先离开已连接状态（Error/Connecting），再等待 5 秒退避后的重连完成。
        awaitState({ it !is ConnectionManager.State.CloudConnected }, timeoutMs = 10_000)
        awaitState({ it is ConnectionManager.State.CloudConnected }, timeoutMs = 25_000)

        val requestLines = mutableListOf<String>()
        while (true) {
            val request = server.takeRequest(500, TimeUnit.MILLISECONDS) ?: break
            requestLines.add(request.requestLine)
        }
        assertTrue(
            requestLines.any { it.startsWith("POST /api/auth/refresh ") },
            "expected refresh request, got $requestLines",
        )
        assertEquals("teacher", ConnectionManager.currentUser.value?.username)
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
