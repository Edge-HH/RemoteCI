package com.remoteci.mobile.calendar

import android.Manifest
import android.app.job.JobInfo
import android.app.job.JobScheduler
import android.content.ComponentName
import android.content.ContentProviderOperation
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.CalendarContract
import android.provider.CalendarContract.Calendars
import android.provider.CalendarContract.Events
import android.provider.CalendarContract.Reminders
import androidx.core.content.ContextCompat
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.SettingsStore
import com.remoteci.mobile.data.normalizeServerUrl
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/**
 * 与网页端两种订阅对应的两个本地日历：我的日程，以及固定绑定一个班级的班级课表。
 * 我的日程沿用最初的无前缀偏好键与日历名，升级后无需重新导入。
 */
enum class CalendarFeed(internal val prefix: String, internal val calendarName: String) {
    Personal("", "RemoteCI"),
    Class("class.", "RemoteCI-class");

    internal fun key(name: String) = prefix + name
}

data class CalendarSyncSettings(
    val automatic: Boolean = false,
    val reminderMinutes: Int = 10,
    val classId: String? = null,
    val className: String? = null,
)

/** 每个账号每种订阅对应一个本地日历；系统日历负责提醒，后台任务只负责刷新课程。 */
object CalendarSync {
    private const val JobId = 24017
    private val mutex = Mutex()
    private val configurationLock = Any()
    val status = MutableStateFlow<Map<CalendarFeed, String>>(emptyMap())
    val busy = MutableStateFlow(false)
    private fun prefs(context: Context) = context.getSharedPreferences("calendar_sync", Context.MODE_PRIVATE)
    private fun owner(server: String, userId: String): String = MessageDigest.getInstance("SHA-256")
        .digest("${normalizeServerUrl(server)}\n$userId".toByteArray()).joinToString("") { "%02x".format(it) }
    private fun setStatus(feed: CalendarFeed, value: String?) =
        status.update { if (value == null) it - feed else it + (feed to value) }

    fun hasPermission(context: Context): Boolean = listOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR)
        .all { ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED }

    fun settings(context: Context, feed: CalendarFeed, server: String, userId: String): CalendarSyncSettings {
        val prefs = prefs(context)
        if (prefs.getString(feed.key("owner"), null) != owner(server, userId)) return CalendarSyncSettings()
        return CalendarSyncSettings(
            prefs.getBoolean(feed.key("automatic"), false), prefs.getInt(feed.key("reminder"), 10),
            prefs.getString(feed.key("classId"), null), prefs.getString(feed.key("className"), null),
        )
    }

    fun configure(context: Context, feed: CalendarFeed, server: String, userId: String, settings: CalendarSyncSettings): Unit = synchronized(configurationLock) {
        require(settings.reminderMinutes in listOf(0, 5, 10, 15, 30))
        require(feed != CalendarFeed.Class || !settings.classId.isNullOrBlank() && !settings.className.isNullOrBlank()) { "请选择要同步的班级" }
        prefs(context).edit().putString(feed.key("owner"), owner(server, userId))
            .putString(feed.key("server"), normalizeServerUrl(server)).putString(feed.key("user"), userId)
            .putBoolean(feed.key("automatic"), settings.automatic).putInt(feed.key("reminder"), settings.reminderMinutes)
            .putString(feed.key("classId"), settings.classId).putString(feed.key("className"), settings.className)
            .putString(feed.key("revision"), UUID.randomUUID().toString()).commit()
        schedule(context)
        setStatus(feed, null)
    }

    fun disable(context: Context): Unit = synchronized(configurationLock) {
        val editor = prefs(context).edit()
        CalendarFeed.entries.forEach { editor.putBoolean(it.key("automatic"), false).putString(it.key("revision"), UUID.randomUUID().toString()) }
        editor.apply()
        context.getSystemService(JobScheduler::class.java).cancel(JobId)
        status.value = emptyMap()
    }

    /** 任一订阅开启自动同步时保留同一个周期任务，任务内逐个同步。 */
    private fun schedule(context: Context) {
        val scheduler = context.getSystemService(JobScheduler::class.java)
        if (CalendarFeed.entries.any { prefs(context).getBoolean(it.key("automatic"), false) }) {
            check(scheduler.schedule(JobInfo.Builder(JobId, ComponentName(context, CalendarSyncJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setPeriodic(15 * 60_000L)
                .setPersisted(true).build()) == JobScheduler.RESULT_SUCCESS) { "无法安排后台同步，请重新尝试" }
        } else scheduler.cancel(JobId)
    }

    suspend fun syncIfEnabled(context: Context): Boolean {
        var success = true
        CalendarFeed.entries.forEach { feed ->
            if (prefs(context).getBoolean(feed.key("automatic"), false)) success = sync(context, feed, onlyIfEnabled = true) && success
        }
        return success
    }

    suspend fun sync(context: Context, feed: CalendarFeed, onlyIfEnabled: Boolean = false): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            val prefs = prefs(context)
            if (onlyIfEnabled && !prefs.getBoolean(feed.key("automatic"), false)) return@withLock true
            val revision = prefs.getString(feed.key("revision"), null) ?: return@withLock false
            val account = prefs.getString(feed.key("owner"), null) ?: return@withLock false
            val server = prefs.getString(feed.key("server"), null) ?: return@withLock false
            val user = prefs.getString(feed.key("user"), null) ?: return@withLock false
            val classId = prefs.getString(feed.key("classId"), null)
            val className = prefs.getString(feed.key("className"), null)
            busy.value = true
            try {
                check(hasPermission(context)) { "请授予日历读写权限后重新导入" }
                val settings = SettingsStore(context).load()
                check(normalizeServerUrl(settings.cloudServerUrl) == server) { "服务器已变更，请为当前账号重新导入" }
                val now = System.currentTimeMillis()
                // 先完整校验，再写入；请求失败或格式错误不会删除已有事件。
                val desired = when (feed) {
                    CalendarFeed.Personal -> calendarEvents(ConnectionManager.calendarSchedule(settings, user), java.time.Instant.ofEpochMilli(now))
                    CalendarFeed.Class -> classCalendarEvents(
                        ConnectionManager.calendarClassSchedule(settings, user, checkNotNull(classId) { "请选择要同步的班级" }),
                        classId, checkNotNull(className) { "请选择要同步的班级" }, java.time.Instant.ofEpochMilli(now),
                    )
                }
                if (prefs.getString(feed.key("revision"), null) != revision || !ConnectionManager.hasSavedSession()) return@withLock false
                val calendarId = calendar(context, account, feed,
                    if (feed == CalendarFeed.Class) "RemoteCI $className" else "RemoteCI 我的日程")
                val existing = readEvents(context, account, calendarId)
                val changes = calendarChanges(existing, desired, now)
                val operations = arrayListOf<ContentProviderOperation>()
                changes.deletes.forEach { id ->
                    operations += ContentProviderOperation.newDelete(adapter(ContentUris.withAppendedId(Events.CONTENT_URI, id), account)).build()
                }
                changes.updates.forEach { (id, event) ->
                    operations += ContentProviderOperation.newUpdate(adapter(ContentUris.withAppendedId(Events.CONTENT_URI, id), account))
                        .withValues(values(calendarId, event)).build()
                }
                val reminder = prefs.getInt(feed.key("reminder"), 10)
                val desiredKeys = desired.map { it.key }.toSet()
                val retained = existing.filter { it.event.key in desiredKeys && it.id !in changes.deletes }
                val reminders = readReminders(context, retained.map { it.id }.toSet())
                // 只有提醒发生变化才改写，避免周期同步反复重置系统提醒。
                retained
                    .filter { reminders[it.id] != listOf(reminder to Reminders.METHOD_ALERT) }
                    .forEach { stored ->
                        operations += ContentProviderOperation.newDelete(Reminders.CONTENT_URI)
                            .withSelection("${Reminders.EVENT_ID}=?", arrayOf(stored.id.toString())).build()
                        operations += ContentProviderOperation.newInsert(Reminders.CONTENT_URI)
                            .withValue(Reminders.EVENT_ID, stored.id).withValue(Reminders.MINUTES, reminder)
                            .withValue(Reminders.METHOD, Reminders.METHOD_ALERT).build()
                    }
                changes.inserts.forEach { event ->
                    val eventOperation = operations.size
                    operations += ContentProviderOperation.newInsert(adapter(Events.CONTENT_URI, account))
                        .withValues(values(calendarId, event)).build()
                    operations += ContentProviderOperation.newInsert(Reminders.CONTENT_URI)
                        .withValueBackReference(Reminders.EVENT_ID, eventOperation)
                        .withValue(Reminders.MINUTES, reminder).withValue(Reminders.METHOD, Reminders.METHOD_ALERT).build()
                }
                val coroutineContext = currentCoroutineContext()
                synchronized(configurationLock) {
                    coroutineContext.ensureActive()
                    if (prefs.getString(feed.key("revision"), null) != revision) return@withLock false
                    if (normalizeServerUrl(SettingsStore(context).load().cloudServerUrl) != server) return@withLock false
                    if (operations.isNotEmpty()) context.contentResolver.applyBatch(CalendarContract.AUTHORITY, operations)
                    setStatus(feed, "已同步 ${desired.size} 节课程 · ${java.time.LocalTime.now().withSecond(0).withNano(0)}")
                }
                true
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (prefs.getString(feed.key("revision"), null) == revision) setStatus(feed, "同步失败：${error.message ?: "请稍后重试"}")
                false
            } finally {
                busy.value = false
            }
        }
    }

    private fun adapter(uri: Uri, account: String): Uri = uri.buildUpon()
        .appendQueryParameter(CalendarContract.CALLER_IS_SYNCADAPTER, "true")
        .appendQueryParameter(Calendars.ACCOUNT_NAME, account)
        .appendQueryParameter(Calendars.ACCOUNT_TYPE, CalendarContract.ACCOUNT_TYPE_LOCAL).build()

    private fun calendar(context: Context, account: String, feed: CalendarFeed, displayName: String): Long {
        val resolver = context.contentResolver
        val cursor = checkNotNull(resolver.query(Calendars.CONTENT_URI, arrayOf(Calendars._ID, Calendars.CALENDAR_DISPLAY_NAME),
            "${Calendars.ACCOUNT_NAME}=? AND ${Calendars.ACCOUNT_TYPE}=? AND ${Calendars.NAME}=?",
            arrayOf(account, CalendarContract.ACCOUNT_TYPE_LOCAL, feed.calendarName), null)) { "设备未提供系统日历" }
        cursor.use {
            if (it.moveToFirst()) {
                val id = it.getLong(0)
                // 班级课表改绑其他班级时同步改名，日历应用里显示的始终是当前绑定的班级。
                if (it.getString(1) != displayName) resolver.update(adapter(ContentUris.withAppendedId(Calendars.CONTENT_URI, id), account),
                    ContentValues().apply { put(Calendars.CALENDAR_DISPLAY_NAME, displayName) }, null, null)
                return id
            }
        }
        val values = ContentValues().apply {
            put(Calendars.ACCOUNT_NAME, account); put(Calendars.ACCOUNT_TYPE, CalendarContract.ACCOUNT_TYPE_LOCAL)
            put(Calendars.NAME, feed.calendarName); put(Calendars.CALENDAR_DISPLAY_NAME, displayName)
            put(Calendars.CALENDAR_COLOR, (if (feed == CalendarFeed.Class) 0xFF3B6939 else 0xFF6750A4).toInt())
            put(Calendars.CALENDAR_ACCESS_LEVEL, Calendars.CAL_ACCESS_OWNER)
            put(Calendars.OWNER_ACCOUNT, account); put(Calendars.VISIBLE, 1); put(Calendars.SYNC_EVENTS, 1)
            put(Calendars.CALENDAR_TIME_ZONE, java.time.ZoneId.systemDefault().id)
            put(Calendars.MAX_REMINDERS, 1); put(Calendars.ALLOWED_REMINDERS, Reminders.METHOD_ALERT.toString())
        }
        return ContentUris.parseId(checkNotNull(resolver.insert(adapter(Calendars.CONTENT_URI, account), values)) { "无法创建系统日历" })
    }

    private fun values(calendarId: Long, event: CalendarEvent) = ContentValues().apply {
        put(Events.CALENDAR_ID, calendarId); put(Events._SYNC_ID, event.key)
        put(Events.TITLE, event.title); put(Events.DESCRIPTION, event.description); put(Events.EVENT_LOCATION, event.location)
        put(Events.DTSTART, event.startsAt); put(Events.DTEND, event.endsAt); put(Events.EVENT_TIMEZONE, event.timeZone)
        put(Events.HAS_ALARM, 1); put(Events.ALL_DAY, 0); put(Events.STATUS, Events.STATUS_CONFIRMED)
    }

    private fun readEvents(context: Context, account: String, calendarId: Long): List<StoredCalendarEvent> = buildList {
        checkNotNull(context.contentResolver.query(adapter(Events.CONTENT_URI, account),
            arrayOf(Events._ID, Events._SYNC_ID, Events.TITLE, Events.DESCRIPTION, Events.EVENT_LOCATION, Events.DTSTART, Events.DTEND, Events.EVENT_TIMEZONE),
            "${Events.CALENDAR_ID}=? AND ${Events._SYNC_ID} IS NOT NULL AND ${Events.DELETED}=0", arrayOf(calendarId.toString()), null)
        ) { "无法读取系统日历" }.use { cursor ->
            while (cursor.moveToNext()) add(StoredCalendarEvent(cursor.getLong(0), CalendarEvent(
                cursor.getString(1), cursor.getString(2).orEmpty(), cursor.getString(3).orEmpty(), cursor.getString(4).orEmpty(),
                cursor.getLong(5), cursor.getLong(6), cursor.getString(7).orEmpty(),
            )))
        }
    }

    private fun readReminders(context: Context, eventIds: Set<Long>): Map<Long, List<Pair<Int, Int>>> {
        val result = mutableMapOf<Long, MutableList<Pair<Int, Int>>>()
        // 按本应用事件关联的日历读取，不扫描其他账号的提醒。
        if (eventIds.isEmpty()) return result
        eventIds.chunked(300).forEach { ids ->
            checkNotNull(context.contentResolver.query(Reminders.CONTENT_URI,
                arrayOf(Reminders.EVENT_ID, Reminders.MINUTES, Reminders.METHOD),
                "${Reminders.EVENT_ID} IN (${ids.joinToString(",") { "?" }})", ids.map(Long::toString).toTypedArray(), null)
            ) { "无法读取日历提醒" }.use { cursor ->
                while (cursor.moveToNext()) result.getOrPut(cursor.getLong(0)) { mutableListOf() }.add(cursor.getInt(1) to cursor.getInt(2))
            }
        }
        return result
    }
}
