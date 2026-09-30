package com.remoteci.watch.notif

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.remoteci.watch.R
import com.remoteci.watch.data.ClassEvent
import com.remoteci.watch.data.EventHistory
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.WatchSettings
import com.remoteci.watch.data.receives
import java.util.concurrent.atomic.AtomicInteger

/**
 * 通知+振动助手：课程事件到达时发系统通知并振动。
 */
object NotificationHelper {
    internal const val CHANNEL_ID = "remoteci_class"

    // 通知 ID 用自增序号：哈希作 ID 时不同事件可能碰撞互相覆盖，导致通知丢失。
    private val notificationIds = AtomicInteger(1)

    fun ensureChannel(context: Context) {
        val channel = NotificationChannel(
            CHANNEL_ID,
            context.getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = context.getString(R.string.notification_channel_description)
            enableVibration(true)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    fun handle(context: Context, event: ClassEvent, settings: WatchSettings, history: EventHistory) {
        if (!history.markIfNew(event) || !settings.receives(event)) return
        // Android 13+ 需要通知权限
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val title = when (event.event) {
            Protocol.EVENT_ON_CLASS -> context.getString(R.string.notification_title_on_class)
            Protocol.EVENT_ON_BREAKING -> context.getString(R.string.notification_title_on_breaking)
            Protocol.EVENT_AFTER_SCHOOL -> context.getString(R.string.notification_title_after_school)
            Protocol.EVENT_SCHEDULE_CHANGED -> context.getString(R.string.notification_title_schedule_changed)
            Protocol.EVENT_CUSTOM -> event.subject ?: context.getString(R.string.notification_title_remote_ci)
            Protocol.EVENT_AUTOMATION_NOTIFICATION,
            Protocol.EVENT_PLUGIN_NOTIFICATION -> event.subject ?: context.getString(R.string.notification_title_classisland)
            else -> context.getString(R.string.notification_title_fallback)
        }

        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(event.message ?: event.subject ?: "")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(context).notify(notificationIds.getAndIncrement(), notification)
        vibrate(context)
    }

    private fun vibrate(context: Context) {
        val vibrator = if (Build.VERSION.SDK_INT >= 31) {
            context.getSystemService(Vibrator::class.java)
        } else {
            @Suppress("DEPRECATION")
            context.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
        }
        vibrator.vibrate(VibrationEffect.createOneShot(800, VibrationEffect.DEFAULT_AMPLITUDE))
    }
}
