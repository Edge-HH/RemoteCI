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
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

data class CalendarSyncSettings(val automatic: Boolean = false, val reminderMinutes: Int = 10)

/** 一个账号对应一个本地日历；系统日历负责提醒，后台任务只负责刷新课程。 */
object CalendarSync {
    private const val JobId = 24017
    private val mutex = Mutex()
    private val configurationLock = Any()
    val status = MutableStateFlow<String?>(null)
    val busy = MutableStateFlow(false)
    private fun prefs(context: Context) = context.getSharedPreferences("calendar_sync", Context.MODE_PRIVATE)
    private fun owner(server: String, userId: String): String = MessageDigest.getInstance("SHA-256")
        .digest("${normalizeServerUrl(server)}\n$userId".toByteArray()).joinToString("") { "%02x".format(it) }

    fun hasPermission(context: Context): Boolean = listOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR)
        .all { ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED }

    fun settings(context: Context, server: String, userId: String): CalendarSyncSettings {
        val prefs = prefs(context)
        if (prefs.getString("owner", null) != owner(server, userId)) return CalendarSyncSettings()
        return CalendarSyncSettings(prefs.getBoolean("automatic", false), prefs.getInt("reminder", 10))
    }

    fun configure(context: Context, server: String, userId: String, settings: CalendarSyncSettings): Unit = synchronized(configurationLock) {
        require(settings.reminderMinutes in listOf(0, 5, 10, 15, 30))
        val scheduler = context.getSystemService(JobScheduler::class.java)
        if (settings.automatic) {
            check(scheduler.schedule(JobInfo.Builder(JobId, ComponentName(context, CalendarSyncJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setPeriodic(15 * 60_000L)
                .setPersisted(true).build()) == JobScheduler.RESULT_SUCCESS) { "无法安排后台同步，请重新尝试" }
        } else scheduler.cancel(JobId)
        prefs(context).edit().putString("owner", owner(server, userId))
            .putString("server", normalizeServerUrl(server)).putString("user", userId)
            .putBoolean("automatic", settings.automatic).putInt("reminder", settings.reminderMinutes)
            .putString("revision", UUID.randomUUID().toString()).apply()
        status.value = null
    }

    fun disable(context: Context): Unit = synchronized(configurationLock) {
        prefs(context).edit().putBoolean("automatic", false).putString("revision", UUID.randomUUID().toString()).apply()
        context.getSystemService(JobScheduler::class.java).cancel(JobId)
        status.value = null
    }

    suspend fun syncIfEnabled(context: Context): Boolean {
        if (!prefs(context).getBoolean("automatic", false)) return true
        return sync(context, onlyIfEnabled = true)
    }

    suspend fun sync(context: Context, onlyIfEnabled: Boolean = false): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            val prefs = prefs(context)
            if (onlyIfEnabled && !prefs.getBoolean("automatic", false)) return@withLock true
            val revision = prefs.getString("revision", null) ?: return@withLock false
            val account = prefs.getString("owner", null) ?: return@withLock false
            val server = prefs.getString("server", null) ?: return@withLock false
            val user = prefs.getString("user", null) ?: return@withLock false
            busy.value = true
            try {
                check(hasPermission(context)) { "请授予日历读写权限后重新导入" }
                val settings = SettingsStore(context).load()
                check(normalizeServerUrl(settings.cloudServerUrl) == server) { "服务器已变更，请为当前账号重新导入" }
                val schedule = ConnectionManager.calendarSchedule(settings, user)
                val now = System.currentTimeMillis()
                // 先完整校验，再写入；请求失败或格式错误不会删除已有事件。
                val desired = calendarEvents(schedule, java.time.Instant.ofEpochMilli(now))
                if (prefs.getString("revision", null) != revision || !ConnectionManager.hasSavedSession()) return@withLock false
                val calendarId = calendar(context, account)
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
                val reminder = prefs.getInt("reminder", 10)
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
                    if (prefs.getString("revision", null) != revision) return@withLock false
                    if (normalizeServerUrl(SettingsStore(context).load().cloudServerUrl) != server) return@withLock false
                    if (operations.isNotEmpty()) context.contentResolver.applyBatch(CalendarContract.AUTHORITY, operations)
                    status.value = "已同步 ${desired.size} 节课程 · ${java.time.LocalTime.now().withSecond(0).withNano(0)}"
                }
                true
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (prefs.getString("revision", null) == revision) status.value = "同步失败：${error.message ?: "请稍后重试"}"
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

    private fun calendar(context: Context, account: String): Long {
        val resolver = context.contentResolver
        val cursor = checkNotNull(resolver.query(Calendars.CONTENT_URI, arrayOf(Calendars._ID),
            "${Calendars.ACCOUNT_NAME}=? AND ${Calendars.ACCOUNT_TYPE}=? AND ${Calendars.NAME}=?",
            arrayOf(account, CalendarContract.ACCOUNT_TYPE_LOCAL, "RemoteCI"), null)) { "设备未提供系统日历" }
        cursor.use { if (it.moveToFirst()) return it.getLong(0) }
        val values = ContentValues().apply {
            put(Calendars.ACCOUNT_NAME, account); put(Calendars.ACCOUNT_TYPE, CalendarContract.ACCOUNT_TYPE_LOCAL)
            put(Calendars.NAME, "RemoteCI"); put(Calendars.CALENDAR_DISPLAY_NAME, "RemoteCI 我的日程")
            put(Calendars.CALENDAR_COLOR, 0xFF6750A4.toInt()); put(Calendars.CALENDAR_ACCESS_LEVEL, Calendars.CAL_ACCESS_OWNER)
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
