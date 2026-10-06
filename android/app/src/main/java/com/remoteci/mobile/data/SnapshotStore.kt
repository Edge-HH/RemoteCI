package com.remoteci.mobile.data

import android.content.Context
import android.util.AtomicFile
import java.io.File
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 缓存数据所属的身份：服务器、登录账号与当前班级。三者之一变化时旧数据不得再展示。 */
@Serializable
data class SnapshotOwner(
    val serverUrl: String,
    val userId: String,
    val classId: String? = null,
)

/**
 * 小组件缓存的完整内容。课堂快照与课表按班级归属，老师日程按账号归属；
 * [rebind] 在身份变化时丢弃不属于新身份的部分，保证切换账号、服务器或班级后不会展示上一身份的数据。
 */
@Serializable
data class WidgetCache(
    val owner: SnapshotOwner? = null,
    val snapshot: ClassStateSnapshot? = null,
    val schedule: ScheduleBundle? = null,
    val personalNext: MyNextCourseResponse? = null,
) {
    fun rebind(next: SnapshotOwner?): WidgetCache = when {
        next == null -> WidgetCache()
        owner == null || owner.serverUrl != next.serverUrl || owner.userId != next.userId -> WidgetCache(owner = next)
        owner.classId != next.classId -> copy(owner = next, snapshot = null, schedule = null)
        else -> copy(owner = next)
    }

    /** 未绑定身份时一律丢弃；带班级标识的数据必须属于当前班级，局域网等不带班级的数据归入当前班级。 */
    fun withSnapshot(value: ClassStateSnapshot): WidgetCache =
        if (belongsToCurrentClass(value.classId)) copy(snapshot = value) else this

    fun withSchedule(value: ScheduleBundle): WidgetCache =
        if (belongsToCurrentClass(value.classId)) copy(schedule = value) else this

    fun withPersonalNext(value: MyNextCourseResponse?): WidgetCache =
        if (owner == null) this else copy(personalNext = value)

    private fun belongsToCurrentClass(classId: String?): Boolean =
        owner != null && (classId == null || owner.classId == null || classId == owner.classId)
}

/**
 * 持久化最后一次有效课堂、课表和老师日程，让手机小组件在断网时仍能展示最近内容。
 *
 * 小组件运行在独立的 `:widgetProvider` 进程（小米 Widget 规范），SharedPreferences 跨进程不会刷新，
 * 因此改为单个 JSON 文件：主进程用 [AtomicFile] 原子替换写入，小组件进程每次刷新都重新读取。
 */
class SnapshotStore(context: Context) {
    private val file = AtomicFile(File(context.applicationContext.filesDir, FILE_NAME))

    fun read(): WidgetCache = runCatching {
        json.decodeFromString<WidgetCache>(file.readFully().decodeToString())
    }.getOrDefault(WidgetCache())

    fun load(): ClassStateSnapshot? = read().takeIf { it.owner != null }?.snapshot

    fun loadSchedule(): ScheduleBundle? = read().takeIf { it.owner != null }?.schedule

    fun loadPersonalNext(): MyNextCourseResponse? = read().takeIf { it.owner != null }?.personalNext

    /** 登录身份或当前班级确定/变化时调用；返回 true 表示缓存有变化，调用方应刷新小组件。 */
    fun bindOwner(owner: SnapshotOwner?): Boolean = update { it.rebind(owner) }

    fun save(snapshot: ClassStateSnapshot) {
        update { it.withSnapshot(snapshot) }
    }

    fun saveSchedule(schedule: ScheduleBundle) {
        update { it.withSchedule(schedule) }
    }

    fun savePersonalNext(value: MyNextCourseResponse) {
        update { it.withPersonalNext(value) }
    }

    fun clearPersonalNext() {
        update { it.withPersonalNext(null) }
    }

    fun clear() {
        update { WidgetCache() }
    }

    private fun update(transform: (WidgetCache) -> WidgetCache): Boolean = synchronized(lock) {
        val current = read()
        val next = transform(current)
        if (next == current) return false
        val stream = file.startWrite()
        try {
            stream.write(json.encodeToString(next).encodeToByteArray())
            file.finishWrite(stream)
        } catch (error: Exception) {
            file.failWrite(stream)
            throw error
        }
        true
    }

    companion object {
        private const val FILE_NAME = "widget_cache.json"
        private const val LEGACY_PREFS_NAME = "remoteci_snapshot"
        private val lock = Any()
        private val json = Json { ignoreUnknownKeys = true }

        /** 旧版本把数据存在不带归属信息的 SharedPreferences 中，无法判断属于哪个账号，升级后直接丢弃。 */
        fun deleteLegacy(context: Context) {
            context.applicationContext.deleteSharedPreferences(LEGACY_PREFS_NAME)
        }
    }
}
