package com.remoteci.watch.ui

import android.Manifest
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.wear.compose.material.Text
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.VoiceRecorder
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

@Composable
internal fun VoiceMessageScreen(connectionReady: Boolean, onBack: () -> Unit) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val scope = rememberCoroutineScope()
    var audio by remember { mutableStateOf<ByteArray?>(null) }
    var recording by remember { mutableStateOf(false) }
    var sending by remember { mutableStateOf(false) }
    var requestingPermission by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("每条最长 60 秒") }
    var preview by remember { mutableStateOf<AudioTrack?>(null) }
    val stop = remember { AtomicBoolean(false) }

    fun stopPreview() { preview?.let { runCatching { it.stop() }; it.release() }; preview = null }
    fun start() {
        if (recording || sending || !lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) return
        stopPreview()
        audio = null
        stop.set(false)
        recording = true
        scope.launch {
            try {
                val captured = VoiceRecorder.record(stop) { status = "录音中 $it / 60 秒" }
                audio = captured.takeIf { it.isNotEmpty() }
                status = if (audio == null) "没有录到声音，请重试" else "已录制 ${captured.size / 32000.0} 秒"
            } catch (e: CancellationException) { throw e }
            catch (e: Exception) { status = "录音失败：${e.message}" }
            finally { recording = false }
        }
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        requestingPermission = false
        if (granted) start() else status = "麦克风权限被拒绝，请在系统设置中允许"
    }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) { stop.set(true); stopPreview() }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); stop.set(true); stopPreview() }
    }
    WatchList("语音消息") {
        item { Text(status, color = Color.White) }
        item {
            ActionButton(if (recording) "停止录音" else "开始录音", null,
                !sending && !requestingPermission && (connectionReady || recording), {
                    if (recording) stop.set(true)
                    else if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) start()
                    else { requestingPermission = true; permission.launch(Manifest.permission.RECORD_AUDIO) }
                })
        }
        item {
            ActionButton(if (preview == null) "试听" else "停止试听", null, audio != null && !sending && !recording, {
                if (preview != null) stopPreview()
                else try {
                    val bytes = audio!!
                    val track = AudioTrack.Builder()
                        .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                        .setAudioFormat(AudioFormat.Builder().setSampleRate(16000).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                        .setTransferMode(AudioTrack.MODE_STATIC).setBufferSizeInBytes(bytes.size).build()
                    preview = track
                    check(track.write(bytes, 0, bytes.size) == bytes.size) { "无法加载录音" }
                    track.setNotificationMarkerPosition(bytes.size / 2)
                    track.setPlaybackPositionUpdateListener(object : AudioTrack.OnPlaybackPositionUpdateListener {
                        override fun onMarkerReached(track: AudioTrack) { if (preview === track) stopPreview() }
                        override fun onPeriodicNotification(track: AudioTrack) = Unit
                    })
                    track.play()
                } catch (e: Exception) { stopPreview(); status = "试听失败：${e.message}" }
            })
        }
        item {
            ActionButton(if (sending) "发送中…" else "发送语音", null, audio != null && connectionReady && !recording && !sending, {
                val bytes = audio ?: return@ActionButton
                stopPreview(); sending = true; status = "等待课表端回执…"
                scope.launch {
                    try {
                        val result = ConnectionManager.sendVoiceMessage(bytes)
                        status = result.message
                        if (result.success) audio = null
                    } catch (e: CancellationException) { throw e }
                    catch (e: Exception) { status = "发送失败：${e.message}" }
                    finally { sending = false }
                }
            })
        }
        item { ActionButton("丢弃录音", null, audio != null && !recording && !sending, { stopPreview(); audio = null; status = "录音已丢弃" }, subtle = true) }
        item { ActionButton("返回", null, true, onBack, subtle = true) }
    }
}
