package com.remoteci.watch.data

import android.content.Context
import androidx.core.content.edit
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 持久化最后一次有效课表，让手表在断网时仍能展示最近课程。 */
class SnapshotStore(context: Context) {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private val json = Json { ignoreUnknownKeys = true }

    fun load(): ClassStateSnapshot? {
        val raw = prefs.getString(KEY_SNAPSHOT, null) ?: return null
        return runCatching { json.decodeFromString<ClassStateSnapshot>(raw) }.getOrNull()
    }

    fun save(snapshot: ClassStateSnapshot) {
        prefs.edit {
            putString(KEY_SNAPSHOT, json.encodeToString(snapshot))
        }
    }

    fun loadSchedule(): ScheduleBundle? {
        val raw = prefs.getString(KEY_SCHEDULE, null) ?: return null
        return runCatching { json.decodeFromString<ScheduleBundle>(raw) }.getOrNull()
    }

    fun saveSchedule(schedule: ScheduleBundle) {
        prefs.edit { putString(KEY_SCHEDULE, json.encodeToString(schedule)) }
    }

    private companion object {
        const val PREFS_NAME = "remoteci_snapshot"
        const val KEY_SNAPSHOT = "latest"
        const val KEY_SCHEDULE = "schedule"
    }
}
