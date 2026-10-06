package com.remoteci.mobile.data

import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** 小组件缓存按服务器、账号与班级隔离：切换身份后不得展示上一身份的课堂、课表或个人日程。 */
class WidgetCacheTest {
    private val ownerA = SnapshotOwner("https://ci.example", "user-a", "class-1")
    private val filled = WidgetCache(owner = ownerA)
        .withSnapshot(ClassStateSnapshot(classId = "class-1", currentSubject = "数学"))
        .withSchedule(ScheduleBundle(classId = "class-1", fromDate = "2026-10-05"))
        .withPersonalNext(MyNextCourseResponse(at = "2026-10-05T08:00:00+08:00"))

    @Test
    fun `switching account clears every cached section`() {
        val switched = filled.rebind(ownerA.copy(userId = "user-b"))

        assertEquals("user-b", switched.owner?.userId)
        assertNull(switched.snapshot)
        assertNull(switched.schedule)
        assertNull(switched.personalNext)
    }

    @Test
    fun `switching server clears every cached section`() {
        val switched = filled.rebind(ownerA.copy(serverUrl = "https://other.example"))

        assertNull(switched.snapshot)
        assertNull(switched.personalNext)
    }

    @Test
    fun `logout or invalid session drops the owner and all data`() {
        val cleared = filled.rebind(null)

        assertEquals(WidgetCache(), cleared)
    }

    @Test
    fun `switching class keeps account schedule but drops class data`() {
        val switched = filled.rebind(ownerA.copy(classId = "class-2"))

        assertNull(switched.snapshot)
        assertNull(switched.schedule)
        assertNotNull(switched.personalNext)
    }

    @Test
    fun `same owner keeps data`() {
        assertEquals(filled, filled.rebind(ownerA))
    }

    @Test
    fun `data from another class or before login is not cached`() {
        assertNull(WidgetCache().withSnapshot(ClassStateSnapshot(currentSubject = "语文")).snapshot)
        assertEquals(
            "数学",
            filled.withSnapshot(ClassStateSnapshot(classId = "class-9", currentSubject = "英语")).snapshot?.currentSubject,
        )
        // 局域网插件推送不带班级标识，归入当前班级。
        assertEquals(
            "物理",
            filled.withSnapshot(ClassStateSnapshot(currentSubject = "物理")).snapshot?.currentSubject,
        )
    }

    @Test
    fun `cache owner requires a provable account id`() {
        val anonymous = Json.decodeFromString<UserProfile>("""{"username":"a","displayName":"甲","role":1}""")
        assertNull(widgetCacheOwner("https://ci.example", anonymous, "class-1"))
        assertNull(widgetCacheOwner("https://ci.example", null, "class-1"))
    }

    @Test
    fun `teachers and head teachers both poll personal schedule but only teachers show it in widgets`() {
        val teacher = Json.decodeFromString<UserProfile>(
            """{"id":"u1","username":"wang","displayName":"王老师","role":1,"roleKind":5}""",
        )
        val headTeacher = Json.decodeFromString<UserProfile>(
            """{"id":"u5","username":"zhao","displayName":"赵老师","role":1,"roleKind":4}""",
        )
        val student = Json.decodeFromString<UserProfile>("""{"id":"u9","username":"s","displayName":"学生","role":1}""")

        assertEquals("u1", personalScheduleUserId(teacher))
        assertEquals("u5", personalScheduleUserId(headTeacher))
        assertNull(personalScheduleUserId(student))
        assertTrue(widgetUsesPersonalSchedule(teacher))
        assertFalse(widgetUsesPersonalSchedule(headTeacher))
    }

    @Test
    fun `personal schedule becomes stale only after repeated failures`() {
        assertTrue(isPersonalScheduleStale(lastSuccessAtMs = 0L, nowMs = 1_000L))
        assertFalse(isPersonalScheduleStale(lastSuccessAtMs = 10_000L, nowMs = 10_000L + PersonalScheduleRefreshMs))
        assertTrue(isPersonalScheduleStale(lastSuccessAtMs = 10_000L, nowMs = 10_000L + PersonalScheduleStaleMs))
    }
}
