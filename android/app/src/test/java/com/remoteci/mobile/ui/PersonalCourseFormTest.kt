package com.remoteci.mobile.ui

import com.remoteci.mobile.data.PersonalCourseView
import com.remoteci.mobile.data.SemesterSettingsView
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class PersonalCourseFormTest {
    private val now = ZonedDateTime.of(2026, 10, 22, 9, 0, 0, 0, ZoneId.of("Asia/Shanghai")) // 周四

    @Test fun `new course defaults to today's weekday every week in the phone zone`() {
        val form = courseFormOf(null, now)
        assertEquals(4, form.weekDay)
        assertEquals(LocalTime.of(8, 0), form.start)
        assertEquals("每周", form.rotation.label)
        assertEquals(480, form.offsetMinutes)
    }

    @Test fun `existing weekly course keeps its rule and zone`() {
        val form = courseFormOf(PersonalCourseView(
            id = "a", subject = "晚自习", weekDay = 0, startTime = "19:00", endTime = "20:30",
            weekCountDiv = 2, weekCountDivTotal = 3, timeZoneOffsetMinutes = 540,
            startsAt = "2026-10-25T19:00:00+09:00", endsAt = "2026-10-25T20:30:00+09:00", reminderMinutes = null,
        ), now)
        assertEquals(0, form.weekDay)
        assertEquals(LocalTime.of(20, 30), form.end)
        assertEquals("三周轮换 · 第 2 周", form.rotation.label)
        assertEquals(540, form.offsetMinutes)
        assertEquals("", form.reminder)
        val request = form.toRequest().getOrThrow()
        assertEquals("19:00", request.startTime)
        assertEquals(2, request.weekCountDiv)
        assertEquals(3, request.weekCountDivTotal)
        assertEquals(null, request.reminderMinutes)
    }

    @Test fun `course from an old server is inferred from its absolute time`() {
        val form = courseFormOf(PersonalCourseView(id = "a", subject = "单次", startsAt = "2026-10-17T14:00:00+08:00", endsAt = "2026-10-17T14:45:00+08:00"), now)
        assertEquals(6, form.weekDay)
        assertEquals(LocalTime.of(14, 45), form.end)
    }

    @Test fun `invalid input is rejected before saving`() {
        val form = courseFormOf(null, now).copy(subject = "数学")
        assertTrue(form.copy(subject = " ").toRequest().isFailure)
        assertTrue(form.copy(end = LocalTime.of(7, 0)).toRequest().isFailure)
        assertTrue(form.copy(reminder = "0").toRequest().isFailure)
        assertTrue(form.copy(reminder = "").toRequest().isSuccess)
    }

    @Test fun `semester hint explains week parity`() {
        assertEquals("本周为第 7 教学周（单周）。", semesterHint(SemesterSettingsView("2026-09-01", currentWeek = 7)))
        assertTrue(semesterHint(SemesterSettingsView()).contains("每周"))
    }
}
