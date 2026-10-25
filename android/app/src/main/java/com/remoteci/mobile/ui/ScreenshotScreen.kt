package com.remoteci.mobile.ui

import android.content.ContentValues
import android.content.Context
import android.graphics.BitmapFactory
import android.os.Build
import android.provider.MediaStore
import androidx.compose.foundation.Image
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.ScreenshotInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** 屏幕截图：经服务端让当前班级的教室电脑截屏，显示并可保存到相册；双指缩放查看细节。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScreenshotScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val classId by ConnectionManager.currentClassId.collectAsState()
    var history by remember { mutableStateOf<List<ScreenshotInfo>>(emptyList()) }
    var shown by remember { mutableStateOf<ScreenshotInfo?>(null) }
    var png by remember { mutableStateOf<ByteArray?>(null) }
    var bitmap by remember { mutableStateOf<ImageBitmap?>(null) }
    var busy by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf<String?>(null) }

    fun show(info: ScreenshotInfo) {
        scope.launch {
            shown = info
            status = null
            runCatching { AdminApi.screenshotBytes(info.url) }
                .onSuccess { bytes ->
                    png = bytes
                    bitmap = withContext(Dispatchers.Default) {
                        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
                    }
                }
                .onFailure { status = it.message ?: "截图加载失败" }
        }
    }

    LaunchedEffect(classId) {
        val id = classId ?: return@LaunchedEffect
        history = runCatching { AdminApi.screenshots(id) }.getOrDefault(emptyList())
        history.firstOrNull()?.let(::show)
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("屏幕截图") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(
                "截取教室电脑全部显示器的当前画面，服务端保留 24 小时；每次截图都会写入操作记录。",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(
                onClick = {
                    val id = classId ?: return@Button
                    busy = true
                    status = "正在截图并上传，通常需要几秒…"
                    scope.launch {
                        runCatching { AdminApi.captureScreen(id) }
                            .onSuccess { info ->
                                history = listOf(info) + history.filterNot { it.id == info.id }
                                show(info)
                            }
                            .onFailure { status = it.message ?: "截图失败" }
                        busy = false
                    }
                },
                enabled = !busy && classId != null,
                modifier = Modifier.fillMaxWidth().height(56.dp),
            ) {
                if (busy) CircularProgressIndicator(Modifier.height(24.dp)) else Text("立即截图")
            }
            status?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            val image = bitmap
            val info = shown
            if (image != null && info != null) {
                ZoomableImage(image)
                Text(screenshotCaption(info), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedButton(
                    onClick = {
                        val bytes = png ?: return@OutlinedButton
                        scope.launch {
                            status = if (saveToGallery(context, screenshotFileName(info), bytes)) "已保存到相册" else "保存失败"
                        }
                    },
                    enabled = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text(if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) "保存到相册" else "保存到相册需要 Android 10 及以上") }
            }
            if (history.size > 1) {
                Text("最近截图", style = MaterialTheme.typography.titleSmall)
                history.chunked(2).forEach { pair ->
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        pair.forEach { item ->
                            FilterChip(
                                selected = item.id == shown?.id,
                                onClick = { show(item) },
                                label = { Text(screenshotCaption(item).substringBefore(" · ")) },
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ZoomableImage(image: ImageBitmap) {
    var scale by remember(image) { mutableFloatStateOf(1f) }
    var offset by remember(image) { mutableStateOf(Offset.Zero) }
    Box(
        Modifier
            .fillMaxWidth()
            .aspectRatio(image.width.toFloat() / image.height.coerceAtLeast(1))
            .clipToBounds()
            .pointerInput(image) {
                detectTransformGestures { _, pan, zoom, _ ->
                    scale = (scale * zoom).coerceIn(1f, 6f)
                    offset = if (scale == 1f) Offset.Zero else offset + pan
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Image(
            image,
            contentDescription = "教室电脑屏幕截图",
            modifier = Modifier.fillMaxSize().graphicsLayer {
                scaleX = scale
                scaleY = scale
                translationX = offset.x
                translationY = offset.y
            },
        )
    }
}

/** 通过 MediaStore 写入“图片/RemoteCI”，Android 10 起无需存储权限。 */
private suspend fun saveToGallery(context: Context, name: String, png: ByteArray): Boolean = withContext(Dispatchers.IO) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return@withContext false
    val resolver = context.contentResolver
    val values = ContentValues().apply {
        put(MediaStore.Images.Media.DISPLAY_NAME, name)
        put(MediaStore.Images.Media.MIME_TYPE, "image/png")
        put(MediaStore.Images.Media.RELATIVE_PATH, "Pictures/RemoteCI")
    }
    val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return@withContext false
    runCatching { resolver.openOutputStream(uri)?.use { it.write(png) } ?: error("无法写入") }
        .onFailure { resolver.delete(uri, null, null) }
        .isSuccess
}
