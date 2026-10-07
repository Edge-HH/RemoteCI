package com.remoteci.watch.data

import android.content.Context
import org.json.JSONArray

/** 无论事件是否启用系统通知，都先记录其标识，避免重连后重复提醒。 */
class EventHistory(context: Context) {
    private val prefs = context.getSharedPreferences("remoteci_events", Context.MODE_PRIVATE)

    @Synchronized
    fun markIfNew(event: ClassEvent): Boolean {
        val key = event.id.ifBlank { "${event.event}|${event.occurredAt}|${event.message}" }
        val trimmed = appendEventId(loadIds(), key) ?: return false
        prefs.edit().putString(KEY_IDS, JSONArray(trimmed).toString()).apply()
        return true
    }

    @Synchronized
    fun clear() {
        prefs.edit().remove(KEY_IDS).apply()
    }

    private fun loadIds(): List<String> {
        val encoded = runCatching { prefs.getString(KEY_IDS, null) }.getOrNull()
        if (encoded != null) {
            return runCatching {
                val array = JSONArray(encoded)
                List(array.length()) { index -> array.getString(index) }
            }.getOrDefault(emptyList())
        }
        // 一次性兼容旧版本的 StringSet；新写入始终保持 FIFO 顺序。
        return prefs.getStringSet(KEY_IDS, emptySet()).orEmpty().toList()
    }

    private companion object {
        const val KEY_IDS = "ids"
        const val MAX_IDS = 100
    }
}

/** 有序去重并保留最近事件；独立成纯函数便于锁定 FIFO 淘汰语义。 */
internal fun appendEventId(existing: List<String>, key: String, maxIds: Int = 100): List<String>? {
    if (key in existing) return null
    return (existing + key).takeLast(maxIds)
}
