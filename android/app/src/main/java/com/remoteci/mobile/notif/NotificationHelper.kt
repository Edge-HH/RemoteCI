package com.remoteci.mobile.notif

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.remoteci.mobile.R
import com.remoteci.mobile.MainActivity
import com.remoteci.mobile.data.ClassEvent
import com.remoteci.mobile.data.ClassStateSnapshot
import com.remoteci.mobile.data.EventHistory
import com.remoteci.mobile.data.MyCourseSlot
import com.remoteci.mobile.data.MyNextCourseResponse
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.Swap
import com.remoteci.mobile.data.UserNotification
import com.remoteci.mobile.data.WatchSettings
import com.remoteci.mobile.data.receives
import java.time.Duration
import java.time.OffsetDateTime
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicInteger

/**
 * 通知+振动助手：课程事件到达时发系统通知并振动。
 */
object NotificationHelper {
    internal const val CHANNEL_ID = "remoteci_class"
    internal const val SCHOOL_STATUS_CHANNEL_ID = "remoteci_school_status"
    /** 换课申请单独成渠道：老师可以在系统设置里单独调整或关闭，不影响课程提醒。 */
    internal const val SWAP_CHANNEL_ID = "remoteci_swap"
    /** 点击换课通知时 MainActivity 收到的跳转目标。 */
    const val EXTRA_OPEN = "com.remoteci.mobile.OPEN"
    const val OPEN_SWAP_REQUESTS = "swap_requests"
    private const val SCHOOL_STATUS_NOTIFICATION_ID = 1001
    private const val SCHOOL_STATUS_UPDATE_INTERVAL_MS = 15_000L
    @Volatile private var lastSchoolStatusAt = 0L
    @Volatile private var lastSchoolStatusTitle: String? = null
    @Volatile private var lastSchoolStatusText: String? = null

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
        val statusChannel = NotificationChannel(
            SCHOOL_STATUS_CHANNEL_ID,
            context.getString(R.string.notification_school_status_channel_name),
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = context.getString(R.string.notification_school_status_channel_description)
            enableVibration(false)
            setSound(null, null)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(statusChannel)
        val swapChannel = NotificationChannel(
            SWAP_CHANNEL_ID,
            context.getString(R.string.notification_swap_channel_name),
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = context.getString(R.string.notification_swap_channel_description)
            enableVibration(true)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(swapChannel)
    }

    /** 个人通知（换课申请、审批结果、强制换课）：按通知 Id 去重，点击打开换课页。 */
    fun handleUser(context: Context, notification: UserNotification, history: EventHistory) {
        if (notification.readAt != null || !history.markIfNew("user:${notification.id}")) return
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) return
        val intent = Intent(context, MainActivity::class.java)
            .putExtra(EXTRA_OPEN, OPEN_SWAP_REQUESTS)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val pending = PendingIntent.getActivity(
            context, notification.id.hashCode(), intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val urgent = notification.kind == Swap.KIND_REQUESTED || notification.kind == Swap.KIND_FORCED
        val built = NotificationCompat.Builder(context, SWAP_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(notification.title.ifBlank { context.getString(R.string.notification_swap_channel_name) })
            .setContentText(notification.body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(notification.body))
            .setPriority(if (urgent) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setAutoCancel(true)
            .setContentIntent(pending)
            .build()
        NotificationManagerCompat.from(context).notify(notificationIds.getAndIncrement(), built)
        if (urgent) vibrate(context)
    }

    /**
     * 发布上课期间的持续状态。普通 Android 通知是基础能力，
     * `miui.focus.param` 让 HyperOS 3 在获得焦点通知权限时显示超级岛/AOD 内容。
     */
    fun updateSchoolStatus(
        context: Context,
        snapshot: ClassStateSnapshot?,
        personal: MyNextCourseResponse?,
        isTeacher: Boolean,
    ) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return

        val content = if (isTeacher) teacherStatus(personal) else classStatus(snapshot)
        val manager = NotificationManagerCompat.from(context)
        if (content == null) {
            if (lastSchoolStatusTitle != null) {
                manager.cancel(SCHOOL_STATUS_NOTIFICATION_ID)
                lastSchoolStatusTitle = null
                lastSchoolStatusText = null
            }
            return
        }
        val now = SystemClock.elapsedRealtime()
        if (content.title == lastSchoolStatusTitle && content.text == lastSchoolStatusText &&
            now - lastSchoolStatusAt < SCHOOL_STATUS_UPDATE_INTERVAL_MS
        ) return
        lastSchoolStatusTitle = content.title
        lastSchoolStatusText = content.text
        lastSchoolStatusAt = now

        val builder = NotificationCompat.Builder(context, SCHOOL_STATUS_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(content.title)
            .setContentText(content.text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(content.text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_EVENT)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setShowWhen(content.endsAt != null)
            .setContentIntent(openAppIntent(context))

        // Android 16 的 Live Updates 要求应用显式请求 promoted ongoing；低版本会忽略该 extras。
        if (Build.VERSION.SDK_INT >= 36) {
            builder.setRequestPromotedOngoing(true)
        }

        content.endsAt?.let { end ->
            val remaining = Duration.between(OffsetDateTime.now(), end).seconds
            if (remaining > 0) {
                builder.setWhen(end.toInstant().toEpochMilli())
                    .setUsesChronometer(true)
                    .setChronometerCountDown(true)
            }
        }
        builder.setExtras(hyperOsExtras(content.title, content.text))
        manager.notify(SCHOOL_STATUS_NOTIFICATION_ID, builder.build())
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

    private fun openAppIntent(context: Context): PendingIntent {
        val intent = context.packageManager.getLaunchIntentForPackage(context.packageName)
            ?: Intent(context, MainActivity::class.java)
        intent.addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context,
            SCHOOL_STATUS_NOTIFICATION_ID,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun hyperOsExtras(title: String, text: String): android.os.Bundle = android.os.Bundle().apply {
        // Xiaomi 官方焦点通知字段；非小米系统会忽略未知 extras。
        val island = JSONObject()
            .put("islandProperty", 1)
            .put("islandOrder", true)
            .put("islandTimeout", 3600)
            .put("bigIslandArea", JSONObject().put("title", title).put("content", text))
            .put("smallIslandArea", JSONObject().put("title", title).put("content", text))
        val params = JSONObject()
            .put("protocol", 1)
            .put("business", "remoteci_school")
            .put("enableFloat", true)
            .put("updatable", true)
            .put("ticker", "$title · $text")
            .put("aodTitle", "$title · $text")
            .put("param_island", island)
        putString("miui.focus.param", JSONObject().put("param_v2", params).toString())
    }

    private data class SchoolStatusContent(
        val title: String,
        val text: String,
        val endsAt: OffsetDateTime? = null,
    )

    private fun classStatus(snapshot: ClassStateSnapshot?): SchoolStatusContent? {
        snapshot ?: return null
        val title = when (snapshot.currentState) {
            Protocol.STATE_CLASS -> "上课中 · ${snapshot.currentSubject ?: "当前课程"}"
            Protocol.STATE_BREAKING -> "课间休息"
            Protocol.STATE_PREPARE_CLASS -> "即将上课 · ${snapshot.nextClassSubject ?: "下一节"}"
            else -> return null
        }
        val remaining = when (snapshot.currentState) {
            Protocol.STATE_CLASS -> snapshot.onClassLeftTime
            Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS -> snapshot.onBreakingLeftTime
            else -> null
        }
        val current = listOfNotNull(snapshot.currentTimeLayoutItem, remaining?.let { "剩余 $it" })
            .joinToString(" · ")
        val next = snapshot.nextClassSubject?.takeIf { it.isNotBlank() }?.let {
            "下一节：$it${snapshot.nextClassTimeLayoutItem?.let { item -> " · ${item.substringBefore(' ')}" } ?: ""}"
        }
        return SchoolStatusContent(title, listOfNotNull(current.takeIf { it.isNotBlank() }, next).joinToString("｜").ifBlank { "RemoteCI 课堂状态" })
    }

    private fun teacherStatus(response: MyNextCourseResponse?): SchoolStatusContent? {
        val current = response?.current
        val next = response?.next
        if (current == null && next == null) return null
        val title = if (current != null) "上课中 · ${current.course.subject}" else "即将上课 · ${next!!.course.subject}"
        val currentText = current?.let { "${it.className} · ${slotRange(it)}" }
        val nextText = next?.let { "下一节：${it.course.subject}（${it.className} · ${slotRange(it)}）" }
        val end = current?.endsAt?.let(::parseTime)
        return SchoolStatusContent(title, listOfNotNull(currentText, nextText).joinToString("｜"), end)
    }

    private fun slotRange(slot: MyCourseSlot): String = listOfNotNull(slot.course.startTime, slot.course.endTime)
        .joinToString("–").ifBlank { "${slot.startsAt.take(16).replace('T', ' ')}" }

    private fun parseTime(value: String): OffsetDateTime? = runCatching { OffsetDateTime.parse(value) }.getOrNull()
}
