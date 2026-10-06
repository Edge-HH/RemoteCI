package com.remoteci.mobile.widget

import android.app.AlarmManager
import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.SystemClock

object WidgetUpdateScheduler {
    const val ACTION_UPDATE = "com.remoteci.mobile.widget.UPDATE"
    private const val REQUEST_CODE = 3601
    private const val PERIOD_MS = 60_000L

    fun schedule(context: Context) {
        val alarm = context.getSystemService(AlarmManager::class.java) ?: return
        alarm.setInexactRepeating(
            AlarmManager.ELAPSED_REALTIME,
            SystemClock.elapsedRealtime() + PERIOD_MS,
            PERIOD_MS,
            pendingIntent(context),
        )
    }

    fun cancel(context: Context) {
        context.getSystemService(AlarmManager::class.java)?.cancel(pendingIntent(context))
    }

    fun cancelIfUnused(context: Context) {
        val appContext = context.applicationContext
        if (!hasWidgets(appContext)) cancel(appContext)
    }

    fun hasWidgets(context: Context): Boolean {
        val appContext = context.applicationContext
        val manager = AppWidgetManager.getInstance(appContext)
        return providers().any { manager.getAppWidgetIds(ComponentName(appContext, it.javaClass)).isNotEmpty() }
    }

    /** 全部小组件类型；新增尺寸或类型时只需在此登记。 */
    fun providers(): List<RemoteCiWidgetProvider> = listOf(
        CurrentStatusWidgetProvider(),
        CurrentStatusWideWidgetProvider(),
        ScheduleWidgetProvider(),
        ScheduleLargeWidgetProvider(),
        SwapWidgetProvider(),
    )

    fun pendingIntent(context: Context): PendingIntent = PendingIntent.getBroadcast(
        context,
        REQUEST_CODE,
        Intent(context, WidgetUpdateReceiver::class.java).setAction(ACTION_UPDATE),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )
}
