package com.remoteci.mobile.data

import java.time.OffsetDateTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** 自动跟随任教班级的判定规则：当前课优先、课前 10 分钟提前切换，自定义课程与已结束的课不参与。 */
class TeachingClassFollowTest {
    private val classA = ClassSummary(id = "11111111-1111-1111-1111-111111111111", name = "高二1班")
    private val classB = ClassSummary(id = "22222222-2222-2222-2222-222222222222", name = "高二3班")
    private val classes = listOf(classA, classB)
    private val now = epoch("09:00")

    @Test
    fun `current course wins over upcoming one`() {
        val next = MyNextCourseResponse(current = slot(classA.id, "08:40", "09:20"), next = slot(classB.id, "09:05", "09:45"))

        assertEquals(classA.id, teachingClassTarget(next, classes, now)?.classId)
    }

    @Test
    fun `upcoming course is followed only within ten minutes`() {
        assertEquals(classB.id, teachingClassTarget(MyNextCourseResponse(next = slot(classB.id, "09:10", "09:50")), classes, now)?.classId)
        assertNull(teachingClassTarget(MyNextCourseResponse(next = slot(classB.id, "09:11", "09:50")), classes, now))
    }

    @Test
    fun `custom courses and inaccessible or finished courses are ignored`() {
        val custom = MyNextCourseResponse(current = slot(PersonalCourseClassId, "08:40", "09:20"), next = slot(classB.id, "09:05", "09:45"))

        assertEquals(classB.id, teachingClassTarget(custom, classes, now)?.classId)
        assertNull(teachingClassTarget(MyNextCourseResponse(current = slot("33333333-3333-3333-3333-333333333333", "08:40", "09:20")), classes, now))
        assertNull(teachingClassTarget(MyNextCourseResponse(current = slot(classA.id, "08:00", "08:45")), classes, now))
    }

    @Test
    fun `manual hold lasts until current course ends or else until next course ends`() {
        val current = slot(classA.id, "08:40", "09:20")
        val upcoming = slot(classB.id, "09:30", "10:10")

        assertEquals(epoch("09:20"), manualClassHoldUntil(MyNextCourseResponse(current = current, next = upcoming), now))
        assertEquals(epoch("10:10"), manualClassHoldUntil(MyNextCourseResponse(next = upcoming), now))
        assertEquals(0L, manualClassHoldUntil(null, now))
    }

    @Test
    fun `only teachers and class administrators with several classes can follow`() {
        assertTrue(canAutoFollowTeachingClass(UserProfile(id = "u", roleKind = Protocol.ROLE_KIND_TEACHER), classes))
        assertTrue(canAutoFollowTeachingClass(UserProfile(id = "u", roleKind = Protocol.ROLE_KIND_CLASS_ADMINISTRATOR), classes))
        assertFalse(canAutoFollowTeachingClass(UserProfile(id = "u", roleKind = Protocol.ROLE_KIND_TEACHER), listOf(classA)))
        assertFalse(canAutoFollowTeachingClass(UserProfile(id = "u"), classes))
    }

    @Test
    fun `notice names the class and the course`() {
        assertEquals("已切换到 高二3班（第3节 数学）", autoClassSwitchNotice("高二3班", slot(classB.id, "09:05", "09:45")))
        assertEquals("已切换到 高二3班", autoClassSwitchNotice("高二3班", slot(classB.id, "09:05", "09:45", label = "", subject = "")))
    }

    private fun slot(classId: String, start: String, end: String, label: String = "第3节", subject: String = "数学") = MyCourseSlot(
        date = "2026-10-19",
        classId = classId,
        className = "班级",
        course = CourseEntry(index = 2, label = label, subjectId = "math", subject = subject),
        startsAt = "2026-10-19T$start:00+08:00",
        endsAt = "2026-10-19T$end:00+08:00",
    )

    private fun epoch(time: String): Long = OffsetDateTime.parse("2026-10-19T$time:00+08:00").toInstant().toEpochMilli()
}
