package com.remoteci.watch.data

import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.Collections
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

internal fun lanPluginCandidate(response: LanDiscoveryResponse, packetSourceHost: String): LanPluginCandidate? {
    if (response.protocolVersion != Protocol.VERSION || response.port !in 1..65535) return null
    if (response.instanceName.isBlank() || packetSourceHost.isBlank()) return null
    return LanPluginCandidate(response.instanceName.trim(), packetSourceHost, response.port)
}

/** UDP 广播发现插件，再通过选中插件的 WebSocket 获取云端引导信息。 */
internal class LanDiscoveryClient(
    private val okHttp: OkHttpClient,
    private val json: Json,
) {
    suspend fun scan(timeoutMillis: Long = 2_500): List<LanPluginCandidate> = withContext(Dispatchers.IO) {
        DatagramSocket(null).use { socket ->
            socket.reuseAddress = true
            socket.broadcast = true
            socket.soTimeout = 200
            socket.bind(InetSocketAddress(0))
            val request = Protocol.LAN_DISCOVERY_REQUEST.encodeToByteArray()
            discoveryBroadcastAddresses().forEach { address ->
                runCatching {
                    socket.send(DatagramPacket(request, request.size, address, Protocol.LAN_DISCOVERY_PORT))
                }
            }

            val found = linkedMapOf<String, LanPluginCandidate>()
            val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
            while (System.nanoTime() < deadline) {
                currentCoroutineContext().ensureActive()
                val buffer = ByteArray(2_048)
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val response = runCatching {
                    json.decodeFromString(
                        LanDiscoveryResponse.serializer(),
                        packet.data.decodeToString(packet.offset, packet.offset + packet.length),
                    )
                }.getOrNull() ?: continue
                val candidate = lanPluginCandidate(response, packet.address.hostAddress ?: continue) ?: continue
                found["${candidate.host}:${candidate.port}"] = candidate
            }
            found.values.toList()
        }
    }

    suspend fun fetchBootstrap(candidate: LanPluginCandidate): ConnectionBootstrapInfo = withTimeout(6_000) {
        // 引导通道同样走明文 ws://，只允许 RFC1918 私网主机。
        requireCleartextPrivateUrl(lanBootstrapUrl(candidate.host, candidate.port))
        suspendCancellableCoroutine { continuation ->
            val listener = object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    val envelope = runCatching { json.decodeFromString(Envelope.serializer(), text) }.getOrNull()
                    if (envelope?.protocolVersion != Protocol.VERSION ||
                        envelope.type != Protocol.TYPE_CONNECTION_BOOTSTRAP
                    ) return
                    val info = envelope.payload?.let {
                        runCatching {
                            json.decodeFromJsonElement(ConnectionBootstrapInfo.serializer(), it)
                        }.getOrNull()
                    } ?: return
                    if (continuation.isActive) continuation.resume(info)
                    webSocket.close(1000, "bootstrap received")
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (continuation.isActive) continuation.resumeWithException(t)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (continuation.isActive)
                        continuation.resumeWithException(IOException("插件未返回云服务器信息"))
                }
            }
            val socket = okHttp.newWebSocket(
                Request.Builder().url(lanBootstrapUrl(candidate.host, candidate.port)).build(),
                listener,
            )
            continuation.invokeOnCancellation { socket.close(1000, "cancelled") }
        }
    }

    private fun discoveryBroadcastAddresses(): List<InetAddress> {
        val addresses = mutableSetOf(InetAddress.getByName("255.255.255.255"))
        runCatching {
            Collections.list(NetworkInterface.getNetworkInterfaces())
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.interfaceAddresses }
                .mapNotNullTo(addresses) { it.broadcast }
        }
        return addresses.toList()
    }
}

private fun lanBootstrapUrl(host: String, port: Int): String =
    lanWebSocketUrl(host, port).removeSuffix("/ws") + "/bootstrap"
