package com.remoteci.mobile.data

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import java.net.URI

internal const val LocalNetworkPermissionMessage =
    "未允许访问本地网络，请在系统设置 → 应用 → RemoteCI → 权限中允许附近的设备，然后重新连接"

/** Android 17 起，INTERNET 权限不再隐含本地网络访问；旧系统无需请求新权限。 */
internal fun hasLocalNetworkPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT < 37 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_LOCAL_NETWORK) ==
        PackageManager.PERMISSION_GRANTED

/** 为明确使用局域网地址的登录请求提前报权限错误，避免让用户白等 TCP 超时。 */
internal fun isLocalServerUrl(url: String): Boolean {
    val host = runCatching { URI(url.trim()).host?.removePrefix("[")?.removeSuffix("]") }
        .getOrNull() ?: return false
    return isCleartextSafeHost(host) || host.endsWith(".local", ignoreCase = true) ||
        host.equals("::1", ignoreCase = true) ||
        host.startsWith("fc", ignoreCase = true) && ':' in host ||
        host.startsWith("fd", ignoreCase = true) && ':' in host ||
        host.startsWith("fe80:", ignoreCase = true)
}
