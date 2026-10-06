package com.remoteci.mobile.widget

import android.appwidget.AppWidgetManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.SystemClock
import java.util.concurrent.atomic.AtomicLong

class WidgetUpdateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        when (intent?.action) {
            WidgetUpdateScheduler.ACTION_UPDATE -> WidgetUpdater.updateAll(context, force = true)
            Intent.ACTION_BOOT_COMPLETED -> {
                if (WidgetUpdateScheduler.hasWidgets(context)) {
                    WidgetUpdateScheduler.schedule(context)
                    WidgetUpdater.updateAll(context, force = true)
                }
            }
        }
    }
}

object WidgetUpdater {
    private const val MIN_PUSH_UPDATE_INTERVAL_MS = 15_000L
    private val lastUpdateAt = AtomicLong(0L)

    fun updateAll(context: Context, force: Boolean = false) {
        val appContext = context.applicationContext
        if (!WidgetUpdateScheduler.hasWidgets(appContext)) return
        val now = SystemClock.elapsedRealtime()
        if (!force) {
            val previous = lastUpdateAt.get()
            if (now - previous < MIN_PUSH_UPDATE_INTERVAL_MS || !lastUpdateAt.compareAndSet(previous, now)) return
        } else {
            lastUpdateAt.set(now)
        }
        val manager = AppWidgetManager.getInstance(appContext)
        WidgetUpdateScheduler.providers().forEach { it.updateWidgets(appContext, manager) }
    }
}
