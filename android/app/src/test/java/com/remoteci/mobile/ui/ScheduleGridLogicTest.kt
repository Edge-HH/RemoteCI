package com.remoteci.mobile.ui

import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.SchedulePeriod
import java.io.File
import java.time.LocalDate
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** 与服务端 ScheduleGridTests 共用 shared/test-fixtures：两端的排布与学科颜色必须逐项一致。 */
class ScheduleGridLogicTest {
    private fun fixture(path: String): JsonObject {
        val url = requireNotNull(javaClass.classLoader?.getResource(path)) { "缺少共享夹具 $path" }
        return Json.parseToJsonElement(url.readText()).jsonObject
    }

    @Test
    fun `grid layout matches shared fixtures`() {
        val directory = File(requireNotNull(javaClass.classLoader?.getResource("schedule-grid")).toURI())
        val files = directory.listFiles { file -> file.extension == "json" }.orEmpty().sortedBy { it.name }
        assertTrue(files.isNotEmpty())
        for (file in files) {
            val fixture = Json.parseToJsonElement(file.readText()).jsonObject
            val periods = fixture.getValue("periods").jsonArray.map {
                val node = it.jsonObject
                SchedulePeriod(
                    index = node.getValue("index").jsonPrimitive.int,
                    startTime = node["startTime"]?.jsonPrimitive?.contentOrNull,
                    endTime = node["endTime"]?.jsonPrimitive?.contentOrNull,
                    dividerBefore = node["dividerBefore"]?.jsonPrimitive?.booleanOrNull ?: false,
                )
            }
            val days = fixture.getValue("days").jsonArray.map {
                val node = it.jsonObject
                GridDay(
                    date = LocalDate.parse(node.getValue("date").jsonPrimitive.content),
                    entries = node.getValue("entries").jsonArray.map { entry -> entry(entry.jsonObject) },
                    dayKind = node["dayKind"]?.jsonPrimitive?.contentOrNull,
                    holidayName = node["holidayName"]?.jsonPrimitive?.contentOrNull,
                    hasData = node["hasData"]?.jsonPrimitive?.booleanOrNull ?: true,
                )
            }
            val grid = ScheduleGridLogic.build(
                periods, days, LocalDate.parse(fixture.getValue("today").jsonPrimitive.content), fixture.getValue("merge").jsonPrimitive.boolean,
            )
            val expected = fixture.getValue("expected").jsonObject
            fun strings(name: String) = expected.getValue(name).jsonArray.map { it.jsonPrimitive.content }
            assertEquals(strings("rows"), grid.rows.map {
                "${it.key}|${it.label}|${it.startTime.orEmpty()}|${it.endTime.orEmpty()}|${if (it.gapBefore) "gap" else "-"}|${if (it.dividerBefore) "div" else "-"}"
            }, file.name)
            assertEquals(strings("columns"), grid.columns.map {
                "${it.date}|${if (it.isToday) 1 else 0}|${it.dayKind ?: "-"}|${if (it.hasData) 1 else 0}"
            }, file.name)
            assertEquals(strings("blocks"), grid.columns.flatMap { column ->
                column.blocks.map { "${column.date}|${it.rowStart}+${it.rowSpan}|${it.lane}/${it.laneCount}|${it.entry.course.subject}|${it.indexes.joinToString(",")}" }
            }, file.name)
            assertEquals(expected.getValue("hasWeekend").jsonPrimitive.boolean, grid.hasWeekendEntries, file.name)
        }
    }

    @Test
    fun `subject colors match shared fixture`() {
        val fixture = fixture("subject-colors.json")
        assertEquals(fixture.getValue("palette").jsonArray.map { it.jsonPrimitive.content }, SubjectColors.palette)
        for (subject in fixture.getValue("subjects").jsonArray.map { it.jsonObject }) {
            val name = subject.getValue("name").jsonPrimitive.content
            assertEquals(subject.getValue("swatch").jsonPrimitive.int, SubjectColors.defaultSwatch(name), name)
            assertEquals(subject.getValue("color").jsonPrimitive.content, SubjectColors.default(name), name)
        }
        for (derived in fixture.getValue("derived").jsonArray.map { it.jsonObject }) {
            val color = derived.getValue("color").jsonPrimitive.content
            for ((mode, dark) in listOf("light" to false, "dark" to true)) {
                val expected = derived.getValue(mode).jsonObject
                assertEquals(
                    expected.getValue("background").jsonPrimitive.content to expected.getValue("foreground").jsonPrimitive.content,
                    SubjectColors.derive(color, dark),
                    "$color $mode",
                )
            }
        }
        assertEquals("#6F9FE0", SubjectColors.normalizePaletteColor(" #6f9fe0 "))
    }

    @Test
    fun `list groups split at profile dividers`() {
        fun course(index: Int) = CourseEntry(index = index, label = "", subjectId = "", subject = "课$index")
        val courses = listOf(course(0), course(1), course(2), course(3))
        assertEquals(listOf(listOf(0, 1), listOf(2, 3)), ScheduleGridLogic.splitAtDividers(courses, listOf(1)).map { group -> group.map { it.index } })
        assertEquals(listOf(listOf(0, 1, 2, 3)), ScheduleGridLogic.splitAtDividers(courses, null).map { group -> group.map { it.index } })
        // 被分割线隔开的两节中间有节次缺席（例如老师只上第 1、4 节）时同样分组。
        assertEquals(listOf(listOf(0), listOf(3)), ScheduleGridLogic.splitAtDividers(listOf(course(0), course(3)), listOf(1)).map { group -> group.map { it.index } })
        assertTrue(ScheduleGridLogic.splitAtDividers(emptyList(), listOf(1)).isEmpty())
    }

    @Test
    fun `week columns follow browsable range`() {
        val monday = LocalDate.parse("2026-11-02")
        assertEquals(monday, mondayOf(LocalDate.parse("2026-11-08")))
        val week = com.remoteci.mobile.data.ScheduleWeekResponse(from = "2026-11-02", minDate = "2026-11-03", maxDate = "2026-11-06")
        assertEquals(listOf(false, true, true, true, true, false, false), classGridDays(week, monday).map { it.hasData })
        assertTrue(classGridDays(null, monday).all { it.hasData && it.entries.isEmpty() })
    }

    private fun entry(node: JsonObject): GridEntry {
        val personal = node["personal"]?.jsonPrimitive?.booleanOrNull ?: false
        val course = CourseEntry(
            index = node["index"]?.jsonPrimitive?.intOrNull ?: 0,
            label = "",
            subjectId = "",
            subject = node.getValue("subject").jsonPrimitive.content,
            startTime = node["startTime"]?.jsonPrimitive?.contentOrNull,
            endTime = node["endTime"]?.jsonPrimitive?.contentOrNull,
            teacher = node["teacher"]?.jsonPrimitive?.contentOrNull,
            location = node["location"]?.jsonPrimitive?.contentOrNull,
            enabled = node["enabled"]?.jsonPrimitive?.booleanOrNull ?: true,
            personalCourseId = if (personal) java.util.UUID.randomUUID().toString() else null,
            adjustmentId = if (node["adjusted"]?.jsonPrimitive?.booleanOrNull == true) "adjusted" else null,
            adjustmentPending = if (node["pending"]?.jsonPrimitive?.booleanOrNull == true) true else null,
        )
        return GridEntry(course, node["classId"]?.jsonPrimitive?.contentOrNull, node["className"]?.jsonPrimitive?.contentOrNull)
    }
}
