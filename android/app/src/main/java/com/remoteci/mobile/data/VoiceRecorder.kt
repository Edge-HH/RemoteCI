package com.remoteci.mobile.data

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.SystemClock
import java.io.ByteArrayOutputStream
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** 只在前台且获得授权后使用；固定 PCM 格式使云端和局域网共用一套协议。 */
internal object VoiceRecorder {
    const val SAMPLE_RATE = 16000
    const val MAX_BYTES = SAMPLE_RATE * 2 * 60

    @SuppressLint("MissingPermission") // 调用者通过运行时权限结果后才开始采集。
    suspend fun record(stop: AtomicBoolean, onSeconds: (Int) -> Unit): ByteArray = withContext(Dispatchers.IO) {
        val minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        check(minimum > 0) { "设备不支持语音录制" }
        val recorder = AudioRecord(MediaRecorder.AudioSource.MIC, SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(minimum, 6400))
        try {
            check(recorder.state == AudioRecord.STATE_INITIALIZED) { "麦克风不可用" }
            recorder.startRecording()
            check(recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) { "麦克风启动失败" }
            val bytes = ByteArrayOutputStream()
            val buffer = ByteArray(3200)
            var reportedSeconds = -1
            val started = SystemClock.elapsedRealtime()
            while (!stop.get() && bytes.size() < MAX_BYTES && SystemClock.elapsedRealtime() - started < 61_000) {
                currentCoroutineContext().ensureActive()
                // 非阻塞读取让取消/退出及时进入 finally，绝不把录音留在后台。
                val count = recorder.read(buffer, 0, minOf(buffer.size, MAX_BYTES - bytes.size()), AudioRecord.READ_NON_BLOCKING)
                check(count >= 0) { "麦克风读取失败" }
                if (count == 0) { delay(20); continue }
                bytes.write(buffer, 0, count)
                val seconds = bytes.size() / (SAMPLE_RATE * 2)
                if (seconds != reportedSeconds) {
                    reportedSeconds = seconds
                    withContext(Dispatchers.Main) { onSeconds(seconds) }
                }
            }
            bytes.toByteArray()
        } finally {
            runCatching { recorder.stop() }
            recorder.release()
        }
    }
}
