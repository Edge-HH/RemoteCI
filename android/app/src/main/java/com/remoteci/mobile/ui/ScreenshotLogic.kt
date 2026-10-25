package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ScreenshotInfo
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private val captionTime = DateTimeFormatter.ofPattern("M月d日 HH:mm:ss")
private val fileTime = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss")

private fun localTime(value: String, zone: ZoneId) =
    runCatching { OffsetDateTime.parse(value).atZoneSameInstant(zone) }.getOrNull()

/** 截图说明：时间 · 设备 · 分辨率 · 大小 · 发起人；时间解析失败时省略。 */
fun screenshotCaption(info: ScreenshotInfo, zone: ZoneId = ZoneId.systemDefault()): String = listOfNotNull(
    localTime(info.capturedAt, zone)?.format(captionTime),
    info.deviceName.takeIf(String::isNotBlank),
    "${info.width}×${info.height}".takeIf { info.width > 0 && info.height > 0 },
    formatScreenshotSize(info.size).takeIf { info.size > 0 },
    info.requestedBy.takeIf(String::isNotBlank),
).joinToString(" · ")

fun formatScreenshotSize(bytes: Long): String =
    if (bytes >= 1024 * 1024) String.format(java.util.Locale.ROOT, "%.1f MB", bytes / 1024.0 / 1024.0)
    else "${maxOf(1L, Math.round(bytes / 1024.0))} KB"

/** 保存到相册的文件名，与 WebUI 下载名一致。 */
fun screenshotFileName(info: ScreenshotInfo, zone: ZoneId = ZoneId.systemDefault()): String =
    "screenshot-${localTime(info.capturedAt, zone)?.format(fileTime) ?: info.id.take(8)}.png"
