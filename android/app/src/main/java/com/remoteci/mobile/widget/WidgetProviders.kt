package com.remoteci.mobile.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.widget.RemoteViews
import com.remoteci.mobile.MainActivity
import com.remoteci.mobile.R
import com.remoteci.mobile.notif.NotificationHelper

class CurrentStatusWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        update(manager, context, ids)
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onEnabled(context: Context) {
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onDisabled(context: Context) {
        WidgetUpdateScheduler.cancelIfUnused(context)
    }

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, appWidgetId: Int, newOptions: Bundle) {
        update(manager, context, intArrayOf(appWidgetId))
    }

    companion object {
        fun update(manager: AppWidgetManager, context: Context, ids: IntArray? = null) {
            val targetIds = ids ?: manager.getAppWidgetIds(ComponentName(context, CurrentStatusWidgetProvider::class.java))
            if (targetIds.isEmpty()) return
            val data = WidgetDataStore.load(context)
            targetIds.forEach { id ->
                val views = RemoteViews(context.packageName, R.layout.widget_current_status)
                val content = statusWidgetContent(data.snapshot, data.personalNext)
                val options = manager.getAppWidgetOptions(id)
                val compact = options.getInt("appWidgetHeight", 0) in 1..149 ||
                    options.getInt("appWidgetWidth", 0) in 1..159
                views.setTextViewText(R.id.widget_status_title, content.stateTitle)
                views.setTextViewText(R.id.widget_status_subject, content.subject)
                views.setTextViewText(R.id.widget_status_period, "${content.period} · ${content.room}".trimEnd(' ', '·'))
                views.setTextViewText(R.id.widget_status_time, content.timeRange)
                views.setTextViewText(R.id.widget_status_next, "下一节：${content.nextLesson}")
                views.setProgressBar(R.id.widget_status_progress, 100, content.progress, false)
                views.setViewVisibility(R.id.widget_status_period, if (compact) android.view.View.GONE else android.view.View.VISIBLE)
                views.setViewVisibility(R.id.widget_status_progress, if (compact) android.view.View.GONE else android.view.View.VISIBLE)
                views.setViewVisibility(R.id.widget_status_next, if (compact) android.view.View.GONE else android.view.View.VISIBLE)
                views.setOnClickPendingIntent(R.id.widget_status_root, openApp(context))
                manager.updateAppWidget(id, views)
            }
        }

        private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
            context,
            3602,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}

class ScheduleWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        update(manager, context, ids)
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onEnabled(context: Context) {
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onDisabled(context: Context) {
        WidgetUpdateScheduler.cancelIfUnused(context)
    }

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, appWidgetId: Int, newOptions: Bundle) {
        update(manager, context, intArrayOf(appWidgetId))
    }

    companion object {
        fun update(manager: AppWidgetManager, context: Context, ids: IntArray? = null) {
            val targetIds = ids ?: manager.getAppWidgetIds(ComponentName(context, ScheduleWidgetProvider::class.java))
            if (targetIds.isEmpty()) return
            val data = WidgetDataStore.load(context)
            targetIds.forEach { id ->
                val options = manager.getAppWidgetOptions(id)
                val all = scheduleWidgetItems(data.snapshot, data.schedule)
                val views = RemoteViews(context.packageName, R.layout.widget_schedule)
                views.removeAllViews(R.id.widget_schedule_courses)
                views.setTextViewText(R.id.widget_schedule_title, "今日课表")
                if (all.isEmpty()) {
                    views.setTextViewText(R.id.widget_schedule_empty, "尚未同步课表")
                    views.setViewVisibility(R.id.widget_schedule_empty, android.view.View.VISIBLE)
                } else {
                    views.setViewVisibility(R.id.widget_schedule_empty, android.view.View.GONE)
                    all.take(scheduleItemCountFor(options, all.size)).forEach { item ->
                        val row = RemoteViews(context.packageName, R.layout.widget_schedule_item)
                        row.setTextViewText(R.id.widget_schedule_item_period, item.period)
                        row.setTextViewText(R.id.widget_schedule_item_subject, item.subject)
                        row.setTextViewText(R.id.widget_schedule_item_time, item.timeRange)
                        views.addView(R.id.widget_schedule_courses, row)
                    }
                }
                views.setOnClickPendingIntent(R.id.widget_schedule_root, openApp(context))
                manager.updateAppWidget(id, views)
            }
        }

        private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
            context,
            3603,
            Intent(context, MainActivity::class.java)
                .putExtra(NotificationHelper.EXTRA_OPEN, NotificationHelper.OPEN_SCHEDULE)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}

class SwapWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        update(manager, context, ids)
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onEnabled(context: Context) {
        WidgetUpdateScheduler.schedule(context)
    }

    override fun onDisabled(context: Context) {
        WidgetUpdateScheduler.cancelIfUnused(context)
    }

    companion object {
        fun update(manager: AppWidgetManager, context: Context, ids: IntArray? = null) {
            val targetIds = ids ?: manager.getAppWidgetIds(ComponentName(context, SwapWidgetProvider::class.java))
            if (targetIds.isEmpty()) return
            targetIds.forEach { id ->
                val views = RemoteViews(context.packageName, R.layout.widget_swap)
                views.setOnClickPendingIntent(R.id.widget_swap_root, openSwap(context))
                manager.updateAppWidget(id, views)
            }
        }

        private fun openSwap(context: Context): PendingIntent = PendingIntent.getActivity(
            context,
            3604,
            Intent(context, MainActivity::class.java)
                .putExtra(NotificationHelper.EXTRA_OPEN, NotificationHelper.OPEN_SWAP_REQUESTS)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}


