package com.remoteci.mobile.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.unit.dp
import com.journeyapps.barcodescanner.ScanContract
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.WebQrScanResponse
import com.remoteci.mobile.data.parseWebLoginQrPayload
import kotlinx.coroutines.launch

/** 待确认的网页登录：挑战码、二维码来自的服务器与浏览器信息。 */
private data class PendingWebLogin(val code: String, val serverUrl: String, val info: WebQrScanResponse)

/**
 * 首页右上角的“扫码登录网页版”：扫描 WebUI 登录页的手机扫码二维码，确认后电脑浏览器直接以当前账号登录。
 * 二维码只含挑战码，真正的登录会话只发给显示二维码的那个浏览器；取消或关闭对话框会通知网页已拒绝。
 */
@Composable
fun WebLoginScanAction(snackbar: SnackbarHostState) {
    val scope = rememberCoroutineScope()
    val user by ConnectionManager.currentUser.collectAsState()
    var pending by remember { mutableStateOf<PendingWebLogin?>(null) }
    var working by remember { mutableStateOf(false) }
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents?.trim().orEmpty()
        if (contents.isEmpty()) return@rememberLauncherForActivityResult
        val payload = parseWebLoginQrPayload(contents)
        if (payload == null) {
            scope.launch { snackbar.showSnackbar("这不是 RemoteCI 网页登录二维码，请扫描 WebUI 登录页“手机扫码”中的二维码") }
            return@rememberLauncherForActivityResult
        }
        scope.launch {
            runCatching { AdminApi.webQrScan(payload.code) }
                .onSuccess { pending = PendingWebLogin(payload.code, payload.serverUrl, it) }
                .onFailure {
                    val current = ConnectionManager.restBaseUrl().orEmpty()
                    val otherServer = !sameServer(payload.serverUrl, current)
                    snackbar.showSnackbar(
                        if (otherServer) "二维码来自 ${payload.serverUrl}，与当前登录的服务器不同"
                        else it.message ?: "二维码已过期，请在网页上刷新后重试",
                    )
                }
        }
    }

    IconButton({ scanner.launch(qrScanOptions("扫描网页登录二维码")) }) {
        Icon(Icons.Rounded.QrCodeScanner, contentDescription = "扫码登录网页版")
    }

    pending?.let { request ->
        fun decide(approve: Boolean) {
            if (working) return
            working = true
            scope.launch {
                val result = runCatching { AdminApi.webQrConfirm(request.code, approve) }
                working = false
                pending = null
                snackbar.showSnackbar(
                    when {
                        result.isFailure -> result.exceptionOrNull()?.message ?: "操作失败，请在网页上刷新二维码"
                        approve -> "已确认，网页即将登录"
                        else -> "已取消网页登录"
                    },
                )
            }
        }
        AlertDialog(
            onDismissRequest = { decide(false) },
            icon = { Icon(Icons.Rounded.Computer, contentDescription = null) },
            title = { Text("登录网页版？") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("将以「${user?.displayName?.ifBlank { user?.username } ?: "当前账号"}」登录 RemoteCI WebUI。")
                    Text("设备：${request.info.browser}", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    request.info.ipAddress?.takeIf(String::isNotBlank)?.let {
                        Text("IP 地址：$it", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Text("请确认是你本人正在操作的电脑；不认识的设备请点“取消”。", style = MaterialTheme.typography.bodySmall)
                }
            },
            confirmButton = { Button({ decide(true) }, enabled = !working) { Text("确认登录") } },
            dismissButton = { TextButton({ decide(false) }, enabled = !working) { Text("取消") } },
        )
    }
}

/** 按主机与端口比较两个服务器地址；协议、末尾斜杠和大小写不影响结果。 */
internal fun sameServer(left: String, right: String): Boolean {
    fun key(value: String): String? = runCatching {
        val uri = java.net.URI(value.trim())
        val port = if (uri.port > 0) uri.port else if (uri.scheme.equals("https", true)) 443 else 80
        "${uri.host?.lowercase()}:$port"
    }.getOrNull()
    val a = key(left)
    return a != null && a == key(right)
}
