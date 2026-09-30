package com.remoteci.watch.data

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

/**
 * 覆盖 ConnectionManager 中与网络无关的纯逻辑：
 * 局域网 HMAC 挑战证明（protocol.md 规范向量）与断线重连指数退避。
 * 这些逻辑原先内嵌在 OkHttp/Android 生命周期里，必须 Robolectric 才能触达；
 * 提取为顶层函数后用普通 JVM 单测锁定协议兼容性。
 */
class ConnectionManagerLogicTest {
    @Test
    fun `lan proof matches server canonical hmac vector`() {
        val challenge = AuthChallenge(challengeId = "c1", nonce = "n1", expiresAt = "2030-01-01T00:00:00Z")
        val session = PersistedDeviceSession(
            username = "teacher",
            deviceSessionId = "ABCDEF12-3456-7890-ABCD-EF1234567890",
            deviceSecret = "secret-value",
            deviceExpiresAt = "2030-01-01T00:00:00Z",
        )
        // 固定 nonce = 字节 0..23，与服务器端 .NET 实现独立计算的向量比对。
        val fixedNonce = ByteArray(24) { it.toByte() }

        val proof = createAuthProof(challenge, session, fixedNonce)

        assertEquals("AAECAwQFBgcICQoLDA0ODxAREhMUFRYX", proof.clientNonce)
        assertEquals("c1", proof.challengeId)
        assertEquals("ABCDEF12-3456-7890-ABCD-EF1234567890", proof.deviceSessionId)
        // canonical = "3|c1|n1|<clientNonce>|<无横线小写 sessionId>"，密钥 = SHA-256(deviceSecret)
        assertEquals("7PhyfRLbHDrbGDrVbAWSkP4GbqgO4QCY7k9JxILypjM=", proof.proof)
    }

    @Test
    fun `lan proof normalizes session id dashes and case`() {
        val challenge = AuthChallenge(challengeId = "c2", nonce = "n2", expiresAt = "2030-01-01T00:00:00Z")
        val fixedNonce = ByteArray(24) { it.toByte() }
        val mixed = PersistedDeviceSession(
            username = "teacher",
            deviceSessionId = "ABCDEF12-3456-7890-ABCD-EF1234567890",
            deviceSecret = "secret-value",
            deviceExpiresAt = "2030-01-01T00:00:00Z",
        )
        val normalized = mixed.copy(deviceSessionId = "abcdef1234567890abcdef1234567890")

        assertEquals(
            createAuthProof(challenge, mixed, fixedNonce).proof,
            createAuthProof(challenge, normalized, fixedNonce).proof,
        )
    }

    @Test
    fun `default client nonce is 24 random bytes`() {
        val challenge = AuthChallenge(challengeId = "c3", nonce = "n3", expiresAt = "2030-01-01T00:00:00Z")
        val session = PersistedDeviceSession(
            username = "teacher",
            deviceSessionId = "abcdef1234567890abcdef1234567890",
            deviceSecret = "secret-value",
            deviceExpiresAt = "2030-01-01T00:00:00Z",
        )

        val first = createAuthProof(challenge, session)
        val second = createAuthProof(challenge, session)

        assertEquals(24, java.util.Base64.getDecoder().decode(first.clientNonce).size)
        assertNotEquals(first.clientNonce, second.clientNonce)
    }

    @Test
    fun `reconnect delay doubles each attempt and caps at max`() {
        assertEquals(10_000L, nextReconnectDelay(InitialReconnectDelayMs))

        var delay = InitialReconnectDelayMs
        val sequence = buildList {
            repeat(6) {
                delay = nextReconnectDelay(delay)
                add(delay)
            }
        }
        assertEquals(listOf(10_000L, 20_000L, 40_000L, 60_000L, 60_000L, 60_000L), sequence)
        assertEquals(60_000L, MaxReconnectDelayMs)
    }
}
