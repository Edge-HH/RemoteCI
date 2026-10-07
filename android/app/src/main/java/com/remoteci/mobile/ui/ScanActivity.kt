package com.remoteci.mobile.ui

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.view.HapticFeedbackConstants
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.FlashlightOff
import androidx.compose.material.icons.rounded.FlashlightOn
import androidx.compose.material.icons.rounded.PhotoLibrary
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.journeyapps.barcodescanner.BarcodeView
import com.journeyapps.barcodescanner.DefaultDecoderFactory
import com.journeyapps.barcodescanner.ScanOptions
import com.google.zxing.client.android.Intents
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 竖屏二维码扫描页，替换 zxing-android-embedded 默认的横屏 CaptureActivity。
 * 结果按 [Intents.Scan.RESULT] 回传，调用方继续使用 ScanContract 解析。
 */
class ScanActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val prompt = intent.getStringExtra(Intents.Scan.PROMPT_MESSAGE).orEmpty().ifBlank { "将二维码放入框内即可自动扫描" }
        setContent {
            ScanScreen(
                prompt = prompt,
                onResult = { text ->
                    setResult(Activity.RESULT_OK, Intent(Intents.Scan.ACTION).putExtra(Intents.Scan.RESULT, text))
                    finish()
                },
                onCancel = { finish() },
            )
        }
    }
}

/** 所有扫码入口共用的参数：仅识别二维码，并使用竖屏扫描页。 */
fun qrScanOptions(prompt: String): ScanOptions = ScanOptions()
    .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
    .setPrompt(prompt)
    .setOrientationLocked(true)
    .setBeepEnabled(false)
    .setCaptureActivity(ScanActivity::class.java)

@Composable
private fun ScanScreen(prompt: String, onResult: (String) -> Unit, onCancel: () -> Unit) {
    val context = LocalContext.current
    val view = LocalView.current
    val scope = rememberCoroutineScope()
    var hasCamera by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    var permissionDenied by remember { mutableStateOf(false) }
    var torchOn by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var done by remember { mutableStateOf(false) }

    fun deliver(text: String) {
        if (done) return
        done = true
        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
        onResult(text)
    }

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        hasCamera = granted
        permissionDenied = !granted
    }
    LaunchedEffect(Unit) { if (!hasCamera) permission.launch(Manifest.permission.CAMERA) }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            val text = withContext(Dispatchers.Default) { runCatching { decodeQrFromImage(context, uri) }.getOrNull() }
            if (text.isNullOrEmpty()) message = "图片中没有识别到二维码" else deliver(text)
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (hasCamera) {
            CameraPreview(torchOn = torchOn, onDecoded = ::deliver)
            ScanOverlay()
        } else {
            Column(
                Modifier.align(Alignment.Center).padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Text(
                    if (permissionDenied) "需要相机权限才能扫码，也可以从相册选择二维码图片。" else "正在请求相机权限…",
                    color = Color.White,
                    textAlign = TextAlign.Center,
                )
                if (permissionDenied) {
                    Button({ permission.launch(Manifest.permission.CAMERA) }) { Text("重新授权") }
                }
            }
        }

        Row(
            Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 8.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onCancel) { Icon(Icons.Rounded.Close, contentDescription = "关闭", tint = Color.White) }
            Text("扫一扫", color = Color.White, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        }

        Column(
            Modifier.align(Alignment.BottomCenter).fillMaxWidth().navigationBarsPadding().padding(bottom = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                message ?: prompt,
                color = if (message != null) Color(0xFFFFB4AB) else Color.White.copy(alpha = 0.9f),
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 32.dp),
            )
            Spacer(Modifier.height(28.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(56.dp)) {
                if (hasCamera) {
                    ScanAction(
                        label = if (torchOn) "关闭手电筒" else "手电筒",
                        icon = { Icon(if (torchOn) Icons.Rounded.FlashlightOn else Icons.Rounded.FlashlightOff, contentDescription = null) },
                        active = torchOn,
                        onClick = { torchOn = !torchOn },
                    )
                }
                ScanAction(
                    label = "相册",
                    icon = { Icon(Icons.Rounded.PhotoLibrary, contentDescription = null) },
                    active = false,
                    onClick = {
                        message = null
                        picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                    },
                )
            }
        }
    }
}

@Composable
private fun ScanAction(label: String, icon: @Composable () -> Unit, active: Boolean, onClick: () -> Unit) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        IconButton(
            onClick = onClick,
            modifier = Modifier.size(56.dp),
            colors = IconButtonDefaults.iconButtonColors(
                containerColor = if (active) Color.White else Color.White.copy(alpha = 0.18f),
                contentColor = if (active) Color.Black else Color.White,
            ),
        ) { icon() }
        Spacer(Modifier.height(8.dp))
        Text(label, color = Color.White, fontSize = 13.sp)
    }
}

@Composable
private fun CameraPreview(torchOn: Boolean, onDecoded: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val barcodeView = remember {
        BarcodeView(context).apply {
            decoderFactory = DefaultDecoderFactory(listOf(BarcodeFormat.QR_CODE))
            // 取景框只是视觉引导，识别区域放宽到几乎整个画面，二维码不必严格对准框内。
            setMarginFraction(0.05)
        }
    }
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_RESUME -> {
                    barcodeView.resume()
                    barcodeView.decodeSingle { result -> result.text?.takeIf { it.isNotBlank() }?.let(onDecoded) }
                }
                Lifecycle.Event.ON_PAUSE -> barcodeView.pause()
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
            observer.onStateChanged(lifecycleOwner, Lifecycle.Event.ON_RESUME)
        }
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            barcodeView.pause()
        }
    }
    LaunchedEffect(torchOn) { barcodeView.setTorch(torchOn) }
    AndroidView(factory = { barcodeView }, modifier = Modifier.fillMaxSize())
}

/** 半透明遮罩 + 圆角取景框 + 上下往返的扫描线。 */
@Composable
private fun ScanOverlay() {
    val transition = rememberInfiniteTransition(label = "scan")
    val progress by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(2000, easing = LinearEasing), RepeatMode.Reverse),
        label = "scanLine",
    )
    Canvas(Modifier.fillMaxSize().graphicsLayer { compositingStrategy = CompositingStrategy.Offscreen }) {
        val side = minOf(size.width, size.height) * 0.7f
        val left = (size.width - side) / 2
        val top = (size.height - side) / 2 - size.height * 0.06f
        val radius = CornerRadius(20.dp.toPx())
        val accent = Color(0xFF4ADE80)

        drawRect(Color.Black.copy(alpha = 0.55f))
        drawRoundRect(Color.Transparent, Offset(left, top), Size(side, side), radius, blendMode = BlendMode.Clear)

        val stroke = 4.dp.toPx()
        val arm = side * 0.12f
        val r = radius.x
        val corners = listOf(
            Triple(Offset(left, top), 1f, 1f),
            Triple(Offset(left + side, top), -1f, 1f),
            Triple(Offset(left, top + side), 1f, -1f),
            Triple(Offset(left + side, top + side), -1f, -1f),
        )
        corners.forEach { (c, dx, dy) ->
            drawLine(accent, Offset(c.x + dx * r, c.y), Offset(c.x + dx * (r + arm), c.y), stroke, StrokeCap.Round)
            drawLine(accent, Offset(c.x, c.y + dy * r), Offset(c.x, c.y + dy * (r + arm)), stroke, StrokeCap.Round)
            drawArc(
                color = accent,
                startAngle = when {
                    dx > 0 && dy > 0 -> 180f
                    dx < 0 && dy > 0 -> 270f
                    dx < 0 && dy < 0 -> 0f
                    else -> 90f
                },
                sweepAngle = 90f,
                useCenter = false,
                topLeft = Offset(if (dx > 0) c.x else c.x - 2 * r, if (dy > 0) c.y else c.y - 2 * r),
                size = Size(2 * r, 2 * r),
                style = androidx.compose.ui.graphics.drawscope.Stroke(stroke, cap = StrokeCap.Round),
            )
        }

        val inset = side * 0.06f
        val lineY = top + inset + (side - 2 * inset) * progress
        drawRect(
            Brush.verticalGradient(listOf(Color.Transparent, accent.copy(alpha = 0.35f)), startY = lineY - 48.dp.toPx(), endY = lineY),
            topLeft = Offset(left + inset, lineY - 48.dp.toPx()),
            size = Size(side - 2 * inset, 48.dp.toPx()),
        )
        drawLine(accent, Offset(left + inset, lineY), Offset(left + side - inset, lineY), 2.dp.toPx(), StrokeCap.Round)
    }
}

/** 从相册图片中识别二维码；大图先缩放，避免解码 OOM。 */
private fun decodeQrFromImage(context: android.content.Context, uri: Uri): String? {
    val resolver = context.contentResolver
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
    var sample = 1
    while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 2048) sample *= 2
    val bitmap = resolver.openInputStream(uri)?.use {
        BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
    } ?: return null
    val pixels = IntArray(bitmap.width * bitmap.height)
    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
    val luminance = RGBLuminanceSource(bitmap.width, bitmap.height, pixels)
    bitmap.recycle()
    val hints = mapOf(
        DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
        DecodeHintType.TRY_HARDER to true,
    )
    return MultiFormatReader().decode(BinaryBitmap(HybridBinarizer(luminance)), hints).text
}
