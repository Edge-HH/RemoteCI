package com.remoteci.mobile.data

import java.io.IOException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlinx.serialization.json.Json
import kotlin.test.assertTrue

class AuthorizationAndNotificationTest {
    @Test
    fun `voice requires explicit capability from both server and plugin`() {
        assertFalse(Protocol.CAP_VOICE_MESSAGE_SEND in Protocol.BASELINE_CAPABILITIES)
        assertFalse(Protocol.CAP_VOICE_MESSAGE_SEND in effectiveCapabilities(CapabilitiesSync(
            server = PeerCapabilities(capabilities = Protocol.CURRENT_CAPABILITIES.toList()),
            plugin = PeerCapabilities(capabilities = Protocol.BASELINE_CAPABILITIES.toList()),
        )))
        assertTrue(Protocol.CAP_VOICE_MESSAGE_SEND in effectiveCapabilities(CapabilitiesSync(
            server = PeerCapabilities(capabilities = Protocol.CURRENT_CAPABILITIES.toList()),
            plugin = PeerCapabilities(capabilities = Protocol.CURRENT_CAPABILITIES.toList()),
        )))
        val command = Json.decodeFromString<CommandMessage>("""{"command":9,"voiceMessage":{"format":"pcm_s16le_16000_mono","audioBase64":"AAA="}}""")
        assertEquals(Protocol.CMD_SEND_VOICE_MESSAGE, command.command)
        assertEquals("AAA=", command.voiceMessage?.audioBase64)
        assertEquals(0, Protocol.PERMISSION_SEND_VOICE_MESSAGES and Protocol.PERMISSION_SEND_NOTIFICATIONS)
    }

    @Test
    fun `effective capabilities require watch server and plugin support`() {
        val effective = effectiveCapabilities(
            CapabilitiesSync(
                server = PeerCapabilities("3.5.0", listOf(Protocol.CAP_SCHEDULE_READ, Protocol.CAP_VOLUME_CONTROL)),
                plugin = PeerCapabilities("3.1.0", listOf(Protocol.CAP_SCHEDULE_READ)),
            ),
        )

        assertEquals(setOf(Protocol.CAP_SCHEDULE_READ), effective)
        assertTrue(effectiveCapabilities(CapabilitiesSync()).isEmpty())
    }

    @Test
    fun `breaking permission model uses protocol version three`() {
        assertEquals(3, Protocol.VERSION)
        assertEquals("REMOTECI_DISCOVER_V3", Protocol.LAN_DISCOVERY_REQUEST)
    }

    @Test
    fun `selected lan plugin fills lan endpoint and reachable cloud url`() {
        val updated = mergeLanBootstrapInfo(
            WatchSettings(),
            LanPluginCandidate("Classroom-PC", "192.168.50.8", 9123),
            ConnectionBootstrapInfo("Classroom-PC", "http://localhost:8080"),
        )

        assertEquals("192.168.50.8", updated.lanHost)
        assertEquals(9123, updated.lanPort)
        assertEquals("http://192.168.50.8:8080", updated.cloudServerUrl)
        assertEquals(
            "http://192.168.50.8:8080",
            mergeLanBootstrapInfo(
                WatchSettings(),
                LanPluginCandidate("Classroom-PC", "192.168.50.8", 9123),
                ConnectionBootstrapInfo("Classroom-PC", "http://0.0.0.0:8080"),
            ).cloudServerUrl,
        )
        assertFailsWith<IllegalArgumentException> {
            mergeLanBootstrapInfo(
                WatchSettings(),
                LanPluginCandidate("Classroom-PC", "192.168.50.8", 9123),
                ConnectionBootstrapInfo("Classroom-PC", "file:///tmp/not-a-server"),
            )
        }
    }

    @Test
    fun `lan discovery candidate uses packet source and rejects incompatible response`() {
        val response = LanDiscoveryResponse(Protocol.VERSION, "Classroom-PC", 9123)

        assertEquals(
            LanPluginCandidate("Classroom-PC", "192.168.50.8", 9123),
            lanPluginCandidate(response, "192.168.50.8"),
        )
        assertNull(lanPluginCandidate(response.copy(protocolVersion = 99), "192.168.50.8"))
        assertNull(lanPluginCandidate(response.copy(port = 0), "192.168.50.8"))
    }

    @Test
    fun `plugin network info updates candidates while preserving a working preferred host`() {
        val current = WatchSettings(lanHost = "10.0.0.8", lanPort = 8765)
        val advertised = PluginNetworkInfo(
            lanServerEnabled = true,
            addresses = listOf("192.168.50.8", "10.0.0.8"),
            port = 9876,
        )

        val updated = mergePluginNetworkInfo(current, advertised)

        assertEquals("10.0.0.8", updated.lanHost)
        assertEquals(9876, updated.lanPort)
        assertEquals(listOf("10.0.0.8", "192.168.50.8"), lanEndpointHosts(updated))
    }

    @Test
    fun `lan url supports editable ports and ipv6 literals`() {
        assertEquals("ws://192.168.1.20:9123/ws", lanWebSocketUrl("192.168.1.20", 9123))
        assertEquals("ws://[fd00::10]:9123/ws", lanWebSocketUrl("fd00::10", 9123))
    }

    @Test
    fun `password login bootstraps cloud even when developer disabled cloud connection`() {
        val settings = WatchSettings(cloudConnectionEnabled = false)

        val plan = planConnection(settings, password = "valid-password")

        assertTrue(plan.bootstrapCloudAuthentication)
        assertFalse(plan.allowCloudFallback)
    }

    @Test
    fun `password login prefers selected lan plugin after cloud authentication`() {
        val plan = planConnection(
            WatchSettings(lanConnectionEnabled = true, lanHost = "192.168.50.8", lanPort = 9123),
            password = "valid-password",
        )

        assertTrue(plan.bootstrapCloudAuthentication)
        assertTrue(plan.preferLanAfterCloudAuthentication)
    }

    @Test
    fun `schedule pull envelope uses dedicated read-only protocol message`() {
        val envelope = schedulePullEnvelope()

        assertEquals(Protocol.TYPE_SCHEDULE_PULL, envelope.type)
        assertTrue(envelope.messageId.isNotBlank())
        val request = Json.decodeFromJsonElement(ScheduleSyncRequest.serializer(), assertNotNull(envelope.payload))
        assertEquals(envelope.messageId, request.taskId)
        assertEquals(Protocol.SCHEDULE_SOURCE_MOBILE, request.source)
    }

    @Test
    fun `new devices receive every supported event type`() {
        val settings = WatchSettings()

        listOf(
            Protocol.EVENT_ON_CLASS,
            Protocol.EVENT_ON_BREAKING,
            Protocol.EVENT_AFTER_SCHOOL,
            Protocol.EVENT_SCHEDULE_CHANGED,
            Protocol.EVENT_CUSTOM,
            Protocol.EVENT_AUTOMATION_NOTIFICATION,
            Protocol.EVENT_PLUGIN_NOTIFICATION,
        ).forEach { eventType ->
            assertTrue(settings.receives(ClassEvent(id = "event-$eventType", event = eventType)))
        }
    }

    @Test
    fun `event preferences are independent`() {
        val settings = WatchSettings(receiveCustom = false, receiveAutomationNotifications = false)

        assertFalse(settings.receives(ClassEvent(id = "custom", event = Protocol.EVENT_CUSTOM)))
        assertTrue(settings.receives(ClassEvent(id = "class", event = Protocol.EVENT_ON_CLASS)))
        assertFalse(settings.receives(ClassEvent(id = "automation", event = Protocol.EVENT_AUTOMATION_NOTIFICATION)))
        assertTrue(settings.receives(ClassEvent(id = "plugin", event = Protocol.EVENT_PLUGIN_NOTIFICATION)))
    }

    @Test
    fun `effective permission gates privileged features`() {
        val user = UserProfile(
            permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )

        assertTrue(user.has(Protocol.PERMISSION_VIEW_CURRENT))
        assertTrue(user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS))
        assertFalse(user.has(Protocol.PERMISSION_MANAGE_SCHEDULE))
    }

    @Test
    fun `cloud websocket url url-encodes base64 access token`() {
        // 服务端令牌是标准 Base64，+ 在查询串里会被解码成空格导致 401。
        assertEquals(
            "wss://ci.example.com/ws?token=Ab%2BCd%2Fe%3D%3D&client=mobile",
            cloudWebSocketUrl("https://ci.example.com", "Ab+Cd/e=="),
        )
        assertEquals(
            "ws://192.168.1.5:8080/ws?token=plainToken&client=mobile",
            cloudWebSocketUrl("http://192.168.1.5:8080/", "plainToken"),
        )
    }

    @Test
    fun `lan endpoint hosts deduplicate and preserve preferred order`() {
        assertEquals(
            listOf("192.168.50.8", "10.0.0.9"),
            lanEndpointHosts(
                WatchSettings(lanHost = "192.168.50.8", lanHostCandidates = listOf("192.168.50.8", "10.0.0.9")),
            ),
        )
        assertEquals(emptyList(), lanEndpointHosts(WatchSettings(lanHost = " ", lanHostCandidates = listOf(" "))))
    }

    @Test
    fun `bootstrap change detection ignores defaults and matches case-insensitively`() {
        // 默认开发地址视为“从未使用”，不触发变更警告。
        assertFalse(bootstrapUrlChanged("http://10.0.2.2:8080", "https://ci.example.com"))
        // 首次引导（无历史记录）必须要求用户显式确认云服务器，防 UDP 发现钓鱼。
        assertTrue(bootstrapUrlChanged("", "https://ci.example.com"))
        // 与上次实际使用的地址不同才警告；大小写与结尾斜杠差异不算变更。
        assertTrue(bootstrapUrlChanged("https://old.example.com", "https://new.example.com"))
        assertFalse(bootstrapUrlChanged("https://old.example.com", "https://OLD.example.com/"))
    }

    @Test
    fun `cleartext is permitted only for rfc1918 ipv4 literals`() {
        assertTrue(isRfc1918Host("192.168.1.100"))
        assertTrue(isRfc1918Host("10.0.2.2"))
        assertTrue(isRfc1918Host("172.16.0.1"))
        assertTrue(isRfc1918Host("172.31.255.254"))
        assertFalse(isRfc1918Host("172.32.0.1"))
        assertFalse(isRfc1918Host("8.8.8.8"))
        assertFalse(isRfc1918Host("ci.example.com"))
        assertFalse(isRfc1918Host("192.168.1"))
        assertFalse(isRfc1918Host("192.168.1.256"))
        assertFalse(isRfc1918Host("192.168.1.+1"))
    }

    @Test
    fun `cleartext urls are rejected outside rfc1918`() {
        // 私网、环回明文与任何 TLS 地址放行。
        requireCleartextPrivateUrl("http://192.168.1.5:8080/api")
        requireCleartextPrivateUrl("ws://10.0.2.2:9123/ws")
        requireCleartextPrivateUrl("ws://localhost:9123/ws")
        requireCleartextPrivateUrl("https://ci.example.com")
        // 公网主机的明文连接一律拒绝。
        assertFailsWith<IOException> { requireCleartextPrivateUrl("http://ci.example.com") }
        assertFailsWith<IOException> { requireCleartextPrivateUrl("ws://8.8.8.8:9123/ws") }
    }

    @Test
    fun `explicit cloud server allows warned public http endpoint`() {
        requireCloudServerUrl("http://203.0.113.10:42089")
        requireCloudServerUrl("https://ci.example.com")
    }
}
