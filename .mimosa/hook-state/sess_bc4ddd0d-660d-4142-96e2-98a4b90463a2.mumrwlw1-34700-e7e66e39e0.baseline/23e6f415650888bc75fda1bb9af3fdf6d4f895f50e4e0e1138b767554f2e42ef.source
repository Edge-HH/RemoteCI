package com.remoteci.watch.data

import android.content.Context

/** 无论事件是否启用系统通知，都先记录其标识，避免重连后重复提醒。 */
class EventHistory(context: Context) {
    private val prefs = context.getSharedPreferences("remoteci_events", Context.MODE_PRIVATE)

    @Synchronized
    fun markIfNew(event: ClassEvent): Boolean {
        val key = event.id.ifBlank { "${event.event}|${event.occurredAt}|${event.message}" }
        val existing = prefs.getStringSet(KEY_IDS, emptySet()).orEmpty().toMutableSet()
        if (!existing.add(key)) return false
        val trimmed = existing.toList().takeLast(MAX_IDS).toSet()
        prefs.edit().putStringSet(KEY_IDS, trimmed).apply()
        return true
    }

    private companion object {
        const val KEY_IDS = "ids"
        const val MAX_IDS = 100
    }
}
