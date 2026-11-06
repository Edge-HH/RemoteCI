package com.remoteci.mobile.ui

import com.remoteci.mobile.data.CourseEntry
import com.remoteci.mobile.data.SchedulePeriod
import java.time.DayOfWeek
import java.time.LocalDate

/** 表格中的一节课与它所在的班级（日程中用于显示班级、跳转到该班课表）。 */
data class GridEntry(val course: CourseEntry, val classId: String? = null, val className: String? = null) {
    val isPersonal: Boolean get() = course.personalCourseId != null
}

/** 表格的一列；hasData 为 false 表示超出可浏览范围，显示“暂无课表数据”。 */
data class GridDay(
    val date: LocalDate,
    val entries: List<GridEntry>,
    val dayKind: String? = null,
    val holidayName: String? = null,
    val hasData: Boolean = true,
)

data class GridRow(
    val key: String,
    val label: String,
    val startTime: String?,
    val endTime: String?,
    val gapBefore: Boolean,
    val dividerBefore: Boolean,
    val extra: Boolean,
)

class GridBlock(val entry: GridEntry, val rowStart: Int, var rowSpan: Int = 1) {
    var lane: Int = 0
    var laneCount: Int = 1
    val indexes: MutableList<Int> = mutableListOf()
}

data class GridColumn(
    val date: LocalDate,
    val isToday: Boolean,
    val dayKind: String?,
    val holidayName: String?,
    val hasData: Boolean,
    val blocks: List<GridBlock>,
) {
    val isWeekend: Boolean get() = date.dayOfWeek == DayOfWeek.SATURDAY || date.dayOfWeek == DayOfWeek.SUNDAY
}

data class GridResult(val rows: List<GridRow>, val columns: List<GridColumn>, val hasWeekendEntries: Boolean)

/**
 * 多彩课程表的排布，是服务端 RemoteCI.Shared.Models.ScheduleGrid 的逐行移植（两端共用 shared/test-fixtures/schedule-grid 校验）：
 * 班级课程按节次放，自定义课程占据与它时间重叠的所有节次行，不与任何节次重叠的按开始时间单独成行；
 * 相邻节次学科、老师、地点、班级都相同且中间没有空白或分割线时合并为连堂；同格撞课左右分道。
 */
object ScheduleGridLogic {
    /** 相邻两段至少空出这么多分钟时留出空白，与服务端 ScheduleTimeAxis.GapMinutes 一致。 */
    const val GAP_MINUTES = 30

    fun build(periods: List<SchedulePeriod>, days: List<GridDay>, today: LocalDate, mergeConsecutive: Boolean): GridResult {
        val rows = periods.map { GridRow("p${it.index}", (it.index + 1).toString(), it.startTime, it.endTime, false, it.dividerBefore, false) }
            .toMutableList()
        val periodRanges = periods.map { minutes(it.startTime) to minutes(it.endTime) }

        val extras = days.flatMap { it.entries }
            .filter { it.isPersonal && periodRanges.none { range -> overlap(it, range.first, range.second) } && minutes(it.course.startTime) != null }
            .groupBy { minutes(it.course.startTime)!! }
            .toSortedMap()
            .map { (start, group) ->
                val end = group.maxOf { minutes(it.course.endTime) ?: start }
                GridRow("x${clock(start)}", "", clock(start), clock(end), false, false, true)
            }
        for (extra in extras) {
            val start = minutes(extra.startTime)!!
            var position = 0
            rows.forEachIndexed { i, row -> minutes(row.startTime)?.let { if (it <= start) position = i + 1 } }
            rows.add(position, extra)
        }
        for (i in 1 until rows.size) rows[i] = rows[i].copy(gapBefore = isGap(rows[i - 1].endTime, rows[i].startTime))

        val rowOfPeriod = HashMap<Int, Int>()
        val rowOfExtra = HashMap<Int, Int>()
        rows.forEachIndexed { i, row ->
            if (!row.extra) rowOfPeriod[row.key.drop(1).toInt()] = i else rowOfExtra[minutes(row.startTime)!!] = i
        }
        val rowRanges = rows.map { Triple(minutes(it.startTime), minutes(it.endTime), it.extra) }

        val columns = days.map { day ->
            var blocks = mutableListOf<GridBlock>()
            for (entry in day.entries.filter { it.course.subject.isNotBlank() }) {
                if (entry.isPersonal) {
                    val covered = rows.indices.filter { !rowRanges[it].third && overlap(entry, rowRanges[it].first, rowRanges[it].second) }
                    if (covered.isNotEmpty()) blocks += GridBlock(entry, covered.first(), covered.last() - covered.first() + 1)
                    else minutes(entry.course.startTime)?.let { start -> rowOfExtra[start]?.let { blocks += GridBlock(entry, it) } }
                } else rowOfPeriod[entry.course.index]?.let { row ->
                    blocks += GridBlock(entry, row).also { it.indexes += entry.course.index }
                }
            }
            if (mergeConsecutive) blocks = merge(blocks, rows)
            val sorted = assignLanes(blocks)
            GridColumn(day.date, day.date == today, day.dayKind, day.holidayName, day.hasData, sorted)
        }
        val weekend = days.any { day ->
            (day.date.dayOfWeek == DayOfWeek.SATURDAY || day.date.dayOfWeek == DayOfWeek.SUNDAY) &&
                day.entries.any { it.course.subject.isNotBlank() }
        }
        return GridResult(rows, columns, weekend)
    }

    /** 连堂合并的依据：学科、老师、地点、班级、启用状态与调课标记都相同。 */
    fun mergeKey(entry: GridEntry): String = listOf(
        SubjectColors.key(entry.course.subject), entry.course.teacher?.trim(), entry.course.location?.trim(), entry.classId,
        entry.course.enabled, entry.course.adjustmentId != null, entry.course.adjustmentPending == true,
    ).joinToString("\u001f")

    private fun merge(blocks: List<GridBlock>, rows: List<GridRow>): MutableList<GridBlock> {
        val occupancy = IntArray(rows.size)
        blocks.forEach { block -> for (i in block.rowStart until block.rowStart + block.rowSpan) occupancy[i]++ }
        val result = mutableListOf<GridBlock>()
        var previous: GridBlock? = null
        for (block in blocks.sortedWith(compareBy<GridBlock> { it.rowStart }.thenBy { it.entry.isPersonal })) {
            val last = previous
            if (last != null && !block.entry.isPersonal && !last.entry.isPersonal &&
                block.rowStart == last.rowStart + last.rowSpan && occupancy[block.rowStart] == 1 &&
                occupancy[last.rowStart + last.rowSpan - 1] == 1 &&
                !rows[block.rowStart].gapBefore && !rows[block.rowStart].dividerBefore &&
                mergeKey(block.entry) == mergeKey(last.entry)
            ) {
                last.rowSpan += block.rowSpan
                last.indexes += block.indexes
                continue
            }
            result += block
            previous = if (block.entry.isPersonal || occupancy[block.rowStart] != 1) null else block
        }
        return result
    }

    /** 稳定排序后按贪心分道，相互重叠的一组色块平分该格宽度；顺序与服务端一致。 */
    private fun assignLanes(blocks: List<GridBlock>): List<GridBlock> {
        val sorted = blocks.sortedWith(
            compareBy<GridBlock> { it.rowStart }.thenByDescending { it.rowSpan }.thenBy { it.entry.isPersonal },
        )
        val cluster = mutableListOf<GridBlock>()
        val laneEnds = mutableListOf<Int>()
        var clusterEnd = -1
        for (block in sorted) {
            if (block.rowStart >= clusterEnd && cluster.isNotEmpty()) {
                cluster.forEach { it.laneCount = laneEnds.size }
                cluster.clear()
                laneEnds.clear()
            }
            var lane = laneEnds.indexOfFirst { it <= block.rowStart }
            if (lane < 0) {
                lane = laneEnds.size
                laneEnds += 0
            }
            laneEnds[lane] = block.rowStart + block.rowSpan
            block.lane = lane
            cluster += block
            clusterEnd = max(if (cluster.size == 1) 0 else clusterEnd, block.rowStart + block.rowSpan)
        }
        cluster.forEach { it.laneCount = laneEnds.size }
        return sorted
    }

    /** 列表视图按分割线把一天的课程分组：dividers 中的 i 表示第 i 节之后有分割线。 */
    fun splitAtDividers(courses: List<CourseEntry>, dividers: List<Int>?): List<List<CourseEntry>> {
        if (dividers.isNullOrEmpty()) return if (courses.isEmpty()) emptyList() else listOf(courses)
        val groups = mutableListOf<MutableList<CourseEntry>>()
        var previous: CourseEntry? = null
        for (course in courses) {
            val split = previous != null && dividers.any { it >= previous!!.index && it < course.index }
            if (groups.isEmpty() || split) groups += mutableListOf<CourseEntry>()
            groups.last() += course
            previous = course
        }
        return groups
    }

    fun isGap(previousEnd: String?, nextStart: String?): Boolean {
        val end = minutes(previousEnd) ?: return false
        val start = minutes(nextStart) ?: return false
        return start - end >= GAP_MINUTES
    }

    /** "HH:mm"（或 "HH:mm:ss"）换算为当天的分钟数；无法解析返回 null。 */
    fun minutes(time: String?): Int? {
        val parts = time?.trim()?.split(':') ?: return null
        if (parts.size !in 2..3) return null
        val hour = parts[0].toIntOrNull() ?: return null
        val minute = parts[1].toIntOrNull() ?: return null
        if (hour !in 0..23 || minute !in 0..59) return null
        return hour * 60 + minute
    }

    private fun overlap(entry: GridEntry, rowStart: Int?, rowEnd: Int?): Boolean {
        val start = minutes(entry.course.startTime) ?: return false
        val end = minutes(entry.course.endTime) ?: return false
        return rowStart != null && rowEnd != null && start < rowEnd && end > rowStart
    }

    private fun clock(minutes: Int): String =
        "${(minutes / 60).toString().padStart(2, '0')}:${(minutes % 60).toString().padStart(2, '0')}"

    private fun max(left: Int, right: Int): Int = if (left > right) left else right
}
