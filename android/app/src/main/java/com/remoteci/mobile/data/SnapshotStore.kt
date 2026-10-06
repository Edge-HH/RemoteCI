package com.remoteci.mobile.data

import android.content.Context
import androidx.core.content.edit
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 持久化最后一次有效课堂、课表和老师日程，让手机小组件在断网时仍能展示最近内容。 */
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

    fun loadPersonalNext(): MyNextCourseResponse? {
        val raw = prefs.getString(KEY_PERSONAL_NEXT, null) ?: return null
        return runCatching { json.decodeFromString<MyNextCourseResponse>(raw) }.getOrNull()
    }

    fun savePersonalNext(value: MyNextCourseResponse) {
        prefs.edit { putString(KEY_PERSONAL_NEXT, json.encodeToString(value)) }
    }

    fun clearPersonalNext() {
        prefs.edit { remove(KEY_PERSONAL_NEXT) }
    }

    fun clear() {
        prefs.edit {
            remove(KEY_SNAPSHOT)
            remove(KEY_SCHEDULE)
            remove(KEY_PERSONAL_NEXT)
        }
    }

    private companion object {
        const val PREFS_NAME = "remoteci_snapshot"
        const val KEY_SNAPSHOT = "latest"
        const val KEY_SCHEDULE = "schedule"
        const val KEY_PERSONAL_NEXT = "personal_next"
    }
}
