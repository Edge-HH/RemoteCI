package com.remoteci.mobile.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.RemoteViews
import com.remoteci.mobile.MainActivity
import com.remoteci.mobile.R
import com.remoteci.mobile.notif.NotificationHelper

/**
 * 所有 RemoteCI 小组件的公共生命周期：系统/小米曝光刷新、尺寸变化、定时刷新的启停。
 *
 * 按小米 HyperOS 小部件规范，receiver 运行在独立的 `:widgetProvider` 进程，只负责读取缓存并渲染，
 * 不拉起主进程；主进程存活时通过 [WidgetUpdater] 主动刷新。receiver 类名一旦上架不得修改。
 */
abstract class RemoteCiWidgetProvider : AppWidgetProvider() {
    /** 为单个小组件实例生成视图；[data] 已按当前登录身份过滤。 */
    protected abstract fun render(context: Context, options: Bundle, data: WidgetSnapshot): RemoteViews

    /** 需要随时间推进刷新（课堂进度、下一节课）的小组件才启用定时刷新。 */
    protected open val needsPeriodicRefresh: Boolean = true

    override fun onReceive(context: Context, intent: Intent) {
        // 小米 Widget 曝光刷新：用户滑到小组件所在页面时由桌面/负一屏发送，代替系统定时刷新。
        if (intent.action == ACTION_MIUI_APPWIDGET_UPDATE) {
            val manager = AppWidgetManager.getInstance(context)
            updateWidgets(context, manager, intent.getIntArrayExtra(AppWidgetManager.EXTRA_APPWIDGET_IDS))
            return
        }
        super.onReceive(context, intent)
    }

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        updateWidgets(context, manager, ids)
        if (needsPeriodicRefresh) WidgetUpdateScheduler.schedule(context)
    }

    override fun onEnabled(context: Context) {
        if (needsPeriodicRefresh) WidgetUpdateScheduler.schedule(context)
    }

    override fun onDisabled(context: Context) {
        WidgetUpdateScheduler.cancelIfUnused(context)
    }

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, appWidgetId: Int, newOptions: Bundle) {
        updateWidgets(context, manager, intArrayOf(appWidgetId))
    }

    /** [ids] 为 null 时刷新本类型的全部实例。清除应用数据后缓存为空，各视图回到未登录的默认状态。 */
    fun updateWidgets(context: Context, manager: AppWidgetManager, ids: IntArray? = null) {
        val targetIds = ids ?: manager.getAppWidgetIds(ComponentName(context, javaClass))
        if (targetIds.isEmpty()) return
        val data = WidgetDataStore.load(context)
        targetIds.forEach { id -> manager.updateAppWidget(id, render(context, manager.getAppWidgetOptions(id), data)) }
    }

    protected fun openApp(context: Context, requestCode: Int, open: String? = null): PendingIntent = PendingIntent.getActivity(
        context,
        requestCode,
        Intent(context, MainActivity::class.java)
            .apply { open?.let { putExtra(NotificationHelper.EXTRA_OPEN, it) } }
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    companion object {
        const val ACTION_MIUI_APPWIDGET_UPDATE = "miui.appwidget.action.APPWIDGET_UPDATE"

        /** 尚未登录或尚未同步：缓存里既没有课堂快照，也没有老师日程。 */
        fun hasNoData(data: WidgetSnapshot): Boolean = data.snapshot == null && data.personalNext == null
    }
}

/** 课堂状态 2x2：当前/下一节课程、时间段与进度；与 4x2 版本同名，小部件中心聚合为一个组件的两种尺寸。 */
open class CurrentStatusWidgetProvider : RemoteCiWidgetProvider() {
    protected open val layout: Int = R.layout.widget_current_status

    override fun render(context: Context, options: Bundle, data: WidgetSnapshot): RemoteViews {
        val views = RemoteViews(context.packageName, layout)
        views.setOnClickPendingIntent(android.R.id.background, openApp(context, 3602))
        if (hasNoData(data)) {
            views.setViewVisibility(R.id.widget_status_content, View.GONE)
            views.setViewVisibility(R.id.widget_status_empty, View.VISIBLE)
            views.setContentDescription(android.R.id.background, context.getString(R.string.widget_status_empty))
            return views
        }
        val content = statusWidgetContent(data.snapshot, data.personalNext)
        val periodLine = "${content.period} · ${content.room}".trimEnd(' ', '·')
        views.setViewVisibility(R.id.widget_status_content, View.VISIBLE)
        views.setViewVisibility(R.id.widget_status_empty, View.GONE)
        views.setTextViewText(R.id.widget_status_title, content.stateTitle)
        views.setTextViewText(R.id.widget_status_subject, content.subject)
        views.setTextViewText(R.id.widget_status_period, periodLine)
        views.setTextViewText(R.id.widget_status_time, content.timeRange)
        views.setTextViewText(R.id.widget_status_next, context.getString(R.string.widget_status_next, content.nextLesson))
        views.setProgressBar(R.id.widget_status_progress, 100, content.progress, false)
        // 5x8 等高密度桌面网格下 2x2 实际尺寸更小：保留课程与时间，收起次要行，避免截断。
        val compact = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 0) in 1..129
        val secondary = if (compact) View.GONE else View.VISIBLE
        views.setViewVisibility(R.id.widget_status_period, secondary)
        views.setViewVisibility(R.id.widget_status_next, secondary)
        // 仅展示信息、整块跳转同一页面：按整块焦点播报（小米无障碍适配要求）。
        views.setContentDescription(
            android.R.id.background,
            listOf(content.stateTitle, content.subject, periodLine, content.timeRange,
                context.getString(R.string.widget_status_next, content.nextLesson))
                .filter(String::isNotBlank).joinToString("，"),
        )
        return views
    }
}

/** 课堂状态 4x2：横向排布，额外展示下一节课。 */
class CurrentStatusWideWidgetProvider : CurrentStatusWidgetProvider() {
    override val layout: Int = R.layout.widget_current_status_wide
}

/** 今日课表 4x2：下一节课及随后课程，按实际高度决定行数；与 4x4 版本同名聚合。 */
open class ScheduleWidgetProvider : RemoteCiWidgetProvider() {
    override fun render(context: Context, options: Bundle, data: WidgetSnapshot): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_schedule)
        views.removeAllViews(R.id.widget_schedule_courses)
        views.setTextViewText(R.id.widget_schedule_title, context.getString(R.string.widget_schedule_name))
        views.setOnClickPendingIntent(android.R.id.background, openApp(context, 3603, NotificationHelper.OPEN_SCHEDULE))
        val all = scheduleWidgetItems(data.snapshot, data.schedule)
        if (all.isEmpty()) {
            val empty = context.getString(
                if (data.snapshot == null && data.schedule == null) R.string.widget_schedule_logged_out
                else R.string.widget_schedule_empty,
            )
            views.setTextViewText(R.id.widget_schedule_empty, empty)
            views.setViewVisibility(R.id.widget_schedule_empty, View.VISIBLE)
            views.setContentDescription(android.R.id.background, empty)
            return views
        }
        views.setViewVisibility(R.id.widget_schedule_empty, View.GONE)
        val shown = all.take(scheduleItemCountFor(options, all.size))
        shown.forEach { item ->
            val row = RemoteViews(context.packageName, R.layout.widget_schedule_item)
            row.setTextViewText(R.id.widget_schedule_item_period, item.period)
            row.setTextViewText(R.id.widget_schedule_item_subject, item.subject)
            row.setTextViewText(R.id.widget_schedule_item_time, item.timeRange)
            views.addView(R.id.widget_schedule_courses, row)
        }
        views.setContentDescription(
            android.R.id.background,
            context.getString(R.string.widget_schedule_name) + "，" +
                shown.joinToString("；") { "${it.period} ${it.subject} ${it.timeRange}" },
        )
        return views
    }
}

/** 今日课表 4x4：同一布局，按高度展示更多课程。 */
class ScheduleLargeWidgetProvider : ScheduleWidgetProvider()

/** 换课 2x2：功能直达换课申请页，内容固定，不需要定时刷新。 */
class SwapWidgetProvider : RemoteCiWidgetProvider() {
    override val needsPeriodicRefresh: Boolean = false

    override fun render(context: Context, options: Bundle, data: WidgetSnapshot): RemoteViews =
        RemoteViews(context.packageName, R.layout.widget_swap).apply {
            setOnClickPendingIntent(
                android.R.id.background,
                openApp(context, 3604, NotificationHelper.OPEN_SWAP_REQUESTS),
            )
        }
}
