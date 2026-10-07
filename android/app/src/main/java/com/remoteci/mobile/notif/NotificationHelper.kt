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
import com.remoteci.mobile.ui.extractTimeRange
import com.remoteci.mobile.ui.nextClassSubjectAfterCurrent
import com.remoteci.mobile.ui.snapshotStageEnd
import java.time.Duration
import java.time.OffsetDateTime
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicInteger

/**
 * 通知+振动助手：课程事件到达时发系统通知并振动。
 */
object NotificationHelper {
    internal const val CHANNEL_ID = "remoteci_class"
    internal const val SCHOOL_STATUS_CHANNEL_ID = "remoteci_school_status_v2"
    private const val LEGACY_SCHOOL_STATUS_CHANNEL_ID = "remoteci_school_status"
    /** 换课申请单独成渠道：老师可以在系统设置里单独调整或关闭，不影响课程提醒。 */
    internal const val SWAP_CHANNEL_ID = "remoteci_swap"
    /** 点击换课通知时 MainActivity 收到的跳转目标。 */
    const val EXTRA_OPEN = "com.remoteci.mobile.OPEN"
    const val OPEN_SWAP_REQUESTS = "swap_requests"
    const val OPEN_SCHEDULE = "schedule"
    private const val SCHOOL_STATUS_NOTIFICATION_ID = 1001
    private const val SCHOOL_STATUS_UPDATE_INTERVAL_MS = 15_000L
    @Volatile private var lastSchoolStatusAt = 0L
    @Volatile private var lastSchoolStatusTitle: String? = null
    @Volatile private var lastSchoolStatusText: String? = null
    @Volatile private var lastSchoolStatusEndsAt: OffsetDateTime? = null

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
        // 渠道创建后声音/振动不可再由应用修改：旧版静音渠道直接删除，换新 ID 让默认值改为全开。
        context.getSystemService(NotificationManager::class.java).deleteNotificationChannel(LEGACY_SCHOOL_STATUS_CHANNEL_ID)
        val statusChannel = NotificationChannel(
            SCHOOL_STATUS_CHANNEL_ID,
            context.getString(R.string.notification_school_status_channel_name),
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = context.getString(R.string.notification_school_status_channel_description)
            enableVibration(true)
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
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
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
                lastSchoolStatusEndsAt = null
            }
            return
        }
        val now = SystemClock.elapsedRealtime()
        if (content.title == lastSchoolStatusTitle && content.text == lastSchoolStatusText &&
            content.endsAt == lastSchoolStatusEndsAt && now - lastSchoolStatusAt < SCHOOL_STATUS_UPDATE_INTERVAL_MS
        ) return
        lastSchoolStatusTitle = content.title
        lastSchoolStatusText = content.text
        lastSchoolStatusEndsAt = content.endsAt
        lastSchoolStatusAt = now

        val builder = NotificationCompat.Builder(context, SCHOOL_STATUS_CHANNEL_ID)
            .setSmallIcon(content.icon)
            .setContentTitle(content.title)
            .setContentText(content.text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(content.text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_EVENT)
            .setOngoing(true)
            // 只在首次出现时响铃振动，后续每 15 秒的倒计时刷新保持安静。
            .setOnlyAlertOnce(true)
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
        // 必须 addExtras：setExtras 会整体替换 Bundle，冲掉上面写入的 promoted ongoing 与倒计时标记。
        builder.addExtras(hyperOsExtras(context, content))
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

        val text = event.message ?: event.subject ?: ""
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setContentIntent(openAppIntent(context))
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

    /**
     * 小米焦点通知（超级岛）参数。岛上 A 区放课程/阶段名，B 区用 `sameWidthDigitInfo.timerInfo`
     * 交给系统倒计时到阶段结束，App 不必每秒重推；非小米系统会忽略未知 extras。
     */
    private fun hyperOsExtras(context: Context, content: SchoolStatusContent): android.os.Bundle {
        val now = System.currentTimeMillis()
        val endMillis = content.endsAt?.toInstant()?.toEpochMilli()?.takeIf { it > now }
        fun timer(end: Long) = JSONObject()
            .put("timerType", -1)
            .put("timerWhen", end)
            .put("timerTotal", 0L)
            .put("timerSystemCurrent", now)
        fun text(title: String) = JSONObject()
            .put("title", title)
            .put("content", "")
            .put("showHighlightColor", false)
            .put("narrowFont", false)

        val bigIsland = JSONObject()
            .put("templateNo", 2)
            .put("imageTextInfoLeft", JSONObject().put("type", 1).put("textInfo", text(content.islandLabel)))
        if (endMillis != null) {
            bigIsland.put(
                "sameWidthDigitInfo",
                JSONObject().put("content", content.timerLabel).put("showHighlightColor", false).put("timerInfo", timer(endMillis)),
            )
        } else {
            bigIsland.put("textInfo", text(content.timerLabel))
        }
        val island = JSONObject()
            .put("islandProperty", 1)
            .put("islandTimeout", 3600)
            .put("bigIslandArea", bigIsland)
            .put("smallIslandArea", JSONObject().put("picInfo", JSONObject().put("type", 1).put("pic", FOCUS_PIC)))
        val params = JSONObject()
            .put("protocol", 1)
            .put("business", "remoteci_school")
            .put("enableFloat", false)
            .put("updatable", true)
            .put("ticker", "${content.title} · ${content.text.replace('\n', ' ')}")
            .put("aodTitle", content.title)
            .put("baseInfo", JSONObject().put("type", 2).put("title", content.title).put("content", content.text))
            .put("param_island", island)
        if (endMillis != null) {
            params.put(
                "hintInfo",
                JSONObject().put("type", 2).put("title", "").put("content", "距离${content.timerLabel}").put("timerInfo", timer(endMillis)),
            )
        }
        return android.os.Bundle().apply {
            putString("miui.focus.param", JSONObject().put("param_v2", params).toString())
            putBundle(
                "miui.focus.pics",
                android.os.Bundle().apply {
                    putParcelable(FOCUS_PIC, android.graphics.drawable.Icon.createWithResource(context, content.icon))
                },
            )
        }
    }

    private const val FOCUS_PIC = "miui.focus.pic_app"

    private data class SchoolStatusContent(
        val title: String,
        val text: String,
        val endsAt: OffsetDateTime? = null,
        /** 超级岛 A 区的短标签，如课程名或“课间”。 */
        val islandLabel: String = title,
        /** 倒计时指向的事件，如“下课”“上课”。 */
        val timerLabel: String = "",
        val inClass: Boolean = false,
    ) {
        /** 上课用日历图标，课间/即将上课用勿扰月亮。 */
        val icon: Int get() = if (inClass) R.drawable.ic_stat_class else R.drawable.ic_stat_break
    }

    private fun classStatus(snapshot: ClassStateSnapshot?): SchoolStatusContent? {
        snapshot ?: return null
        val nextSubject = nextClassSubjectAfterCurrent(snapshot)
        // 超级岛 A 区：课间/即将上课带上下一节课名，如“课间-数学”“即将上课-数学”。
        val (title, islandLabel) = when (snapshot.currentState) {
            Protocol.STATE_CLASS -> (snapshot.currentSubject ?: "当前课程").let { "上课中 · $it" to it }
            Protocol.STATE_BREAKING -> "课间休息" to listOfNotNull("课间", nextSubject).joinToString("-")
            Protocol.STATE_PREPARE_CLASS ->
                "即将上课 · ${nextSubject ?: "下一节"}" to listOfNotNull("即将上课", nextSubject).joinToString("-")
            else -> return null
        }
        val timerLabel = if (snapshot.currentState == Protocol.STATE_CLASS) "下课" else "上课"
        // 剩余时间交给系统计时器倒数；快照里的 *LeftTime 只是推送瞬间的值，写进文本会冻结。
        val current = extractTimeRange(snapshot.currentTimeLayoutItem)
        val next = nextSubject?.let {
            listOf("下一节：$it", extractTimeRange(snapshot.nextClassTimeLayoutItem)).filter(String::isNotBlank).joinToString(" ")
        }
        return SchoolStatusContent(
            title,
            listOfNotNull(current.takeIf { it.isNotBlank() }, next).joinToString("\n").ifBlank { "RemoteCI 课堂状态" },
            snapshotStageEnd(snapshot),
            islandLabel,
            timerLabel,
            inClass = snapshot.currentState == Protocol.STATE_CLASS,
        )
    }

    private fun teacherStatus(response: MyNextCourseResponse?): SchoolStatusContent? {
        val current = response?.current
        val next = response?.next
        if (current == null && next == null) return null
        val title = if (current != null) "上课中 · ${current.course.subject}" else "即将上课 · ${next!!.course.subject}"
        val currentText = current?.let { "${slotRange(it)} · ${it.className}" }
        val nextText = next?.let { "下一节：${it.course.subject} ${slotRange(it)}（${it.className}）" }
        val end = current?.endsAt?.let(::parseTime)
        return SchoolStatusContent(
            title,
            listOfNotNull(currentText, nextText).joinToString("\n"),
            end,
            islandLabel = (current ?: next!!).course.subject,
            timerLabel = if (current != null) "下课" else "上课",
            inClass = current != null,
        )
    }

    private fun slotRange(slot: MyCourseSlot): String = listOfNotNull(slot.course.startTime, slot.course.endTime)
        .joinToString("-").ifBlank { "${slot.startsAt.take(16).replace('T', ' ')}" }

    private fun parseTime(value: String): OffsetDateTime? = runCatching { OffsetDateTime.parse(value) }.getOrNull()
}
