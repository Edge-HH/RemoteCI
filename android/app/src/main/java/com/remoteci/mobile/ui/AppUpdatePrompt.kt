package com.remoteci.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.BuildConfig
import com.remoteci.mobile.data.CompatibleUpdate
import com.remoteci.mobile.data.UpdateManager
import com.remoteci.mobile.data.WatchSettings
import kotlinx.coroutines.launch

/**
 * 启动时自动检查 App 更新：按“更新”页选择的渠道查找比当前更高的版本，发现后弹窗提示。
 * 检查至少间隔 6 小时；用户可以立即更新、稍后提醒或跳过该版本（更高的新版本仍会提示）。
 * 网络失败时静默忽略，不打扰正常使用；“账号与设置 → 更新”可以关闭自动检查。
 */
@Composable
fun AutoUpdatePrompt(settings: WatchSettings, onPersist: (WatchSettings) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val latestSettings by rememberUpdatedState(settings)
    var candidate by remember { mutableStateOf<CompatibleUpdate?>(null) }
    var status by remember { mutableStateOf<String?>(null) }
    var installing by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        val start = latestSettings
        if (!start.autoUpdateCheck) return@LaunchedEffect
        val now = System.currentTimeMillis()
        if (!UpdateManager.shouldAutoCheck(start.lastUpdateCheckAt, now)) return@LaunchedEffect
        val found = runCatching {
            UpdateManager.selectCompatibleUpdate(UpdateManager.fetchReleases(), BuildConfig.VERSION_NAME, start.updateChannel)
        }.getOrElse { return@LaunchedEffect }
        onPersist(latestSettings.copy(lastUpdateCheckAt = now))
        if (UpdateManager.shouldPrompt(found, latestSettings.skippedUpdateVersion)) candidate = found
    }

    val update = candidate ?: return
    val version = UpdateManager.versionFromTag(update.release.tagName)
    AlertDialog(
        onDismissRequest = { if (!installing) candidate = null },
        icon = { Icon(Icons.Rounded.SystemUpdate, contentDescription = null) },
        title = { Text("发现新版本 $version") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("当前版本 ${BuildConfig.VERSION_NAME}，可升级到 $version。", color = MaterialTheme.colorScheme.onSurfaceVariant)
                val notes = UpdateManager.releaseNotesPreview(update.release.body)
                if (notes.isNotBlank()) {
                    Column(Modifier.fillMaxWidth().heightIn(max = 260.dp).verticalScroll(rememberScrollState())) {
                        Text(notes, style = MaterialTheme.typography.bodyMedium)
                    }
                }
                if (installing) LinearProgressIndicator(Modifier.fillMaxWidth())
                status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary) }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    installing = true
                    status = "正在下载 ${update.asset.name}…"
                    scope.launch {
                        runCatching {
                            val file = UpdateManager.downloadApk(context, update.asset)
                            status = "正在安装…"
                            UpdateManager.installApk(context, file)
                        }.onSuccess {
                            status = "请按系统提示完成安装"
                            installing = false
                            candidate = null
                        }.onFailure {
                            installing = false
                            status = it.message ?: "更新失败，可稍后在“更新”页重试"
                        }
                    }
                },
                enabled = !installing,
            ) { Text("立即更新") }
        },
        dismissButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                TextButton(
                    onClick = {
                        onPersist(latestSettings.copy(skippedUpdateVersion = version))
                        candidate = null
                    },
                    enabled = !installing,
                ) { Text("跳过此版本") }
                TextButton(onClick = { candidate = null }, enabled = !installing) { Text("稍后") }
            }
        },
    )
}
