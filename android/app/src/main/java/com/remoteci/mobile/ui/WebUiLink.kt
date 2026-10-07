package com.remoteci.mobile.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import kotlinx.coroutines.launch

/**
 * 页底提示“更多…请使用WebUI”：WebUI 为蓝色超链接，点击后用当前连接的服务器地址
 * 换取一次性网页登录票据，在系统浏览器中打开并自动登录当前账号，落到 returnUrl 页面。
 */
@Composable
fun WebUiHint(prefix: String, returnUrl: String, snackbar: SnackbarHostState?, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var opening by remember { mutableStateOf(false) }
    val linkColor = if (MaterialTheme.colorScheme.surface.luminance() < .5f) Color(0xFF8AB4F8) else Color(0xFF1A73E8)
    val text = buildAnnotatedString {
        append(prefix)
        withLink(
            LinkAnnotation.Clickable(
                tag = "webui",
                styles = TextLinkStyles(SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)),
            ) {
                if (opening) return@Clickable
                scope.launch {
                    opening = true
                    runCatching {
                        val base = ConnectionManager.restBaseUrl() ?: error("尚未连接服务器")
                        // 旧版服务端没有网页登录票据接口（或签发失败）时，仍用当前连接地址直接打开 WebUI，由用户在浏览器里登录。
                        val url = runCatching { AdminApi.webLoginUrl(returnUrl, ConnectionManager.currentClassId.value) }
                            .getOrElse {
                                scope.launch { snackbar?.showSnackbar("服务端暂不支持自动登录，请在浏览器中登录 WebUI") }
                                base + returnUrl
                            }
                        context.startActivity(
                            Intent(Intent.ACTION_VIEW, Uri.parse(url))
                                .addCategory(Intent.CATEGORY_BROWSABLE)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                        )
                    }.onFailure {
                        val message = if (it is ActivityNotFoundException) "没有可用的浏览器" else it.message ?: "无法打开 WebUI"
                        snackbar?.showSnackbar(message)
                    }
                    opening = false
                }
            },
        ) { append("WebUI") }
    }
    Text(
        text,
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = TextAlign.Center,
        modifier = modifier.fillMaxWidth().padding(16.dp),
    )
}
