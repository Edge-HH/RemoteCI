package com.remoteci.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.remoteci.mobile.data.MyScheduleWeekResponse
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.SchedulePeriod
import com.remoteci.mobile.data.ScheduleWeekResponse
import java.time.LocalDate
import kotlin.math.abs

/** 表格的显示设置（本机保存）：显示老师只作用于班级课表，显示班级只作用于我的日程。 */
data class ScheduleGridDisplay(
    val mine: Boolean,
    val showTeacher: Boolean = true,
    val showClass: Boolean = true,
    val showLocation: Boolean = true,
    val weekendMode: String = "auto",
    val merge: Boolean = true,
)

private val PeriodWidth = 40.dp
private val HeaderHeight = 58.dp
private val BaseRowHeight = 66.dp
private val GapHeight = 12.dp
private val DividerHeight = 9.dp
private const val MaxZoom = 2.5f

/**
 * 多彩课程表：行是节次、列是周一到周日，低饱和学科色块。双指缩放（下限一屏放下全部节次，上限 2.5 倍），
 * 放大后单指平移；未放大时左右滑动切换上一周/下一周。缩放结束时经 [onZoomCommit] 保存比例。
 */
@Composable
fun ScheduleWeekGrid(
    periods: List<SchedulePeriod>,
    days: List<GridDay>,
    today: LocalDate,
    display: ScheduleGridDisplay,
    colorOf: (String) -> String,
    zoom: Float,
    onZoomCommit: (Float) -> Unit,
    onSwipeWeek: (Int) -> Unit,
    onBlockClick: (GridBlock, LocalDate) -> Unit,
    modifier: Modifier = Modifier,
) {
    val grid = remember(periods, days, today, display.merge) { ScheduleGridLogic.build(periods, days, today, display.merge) }
    val showWeekend = when (display.weekendMode) {
        "always" -> true
        "never" -> false
        else -> grid.hasWeekendEntries
    }
    val columns = grid.columns.filter { showWeekend || !it.isWeekend }
    val dark = MaterialTheme.colorScheme.surface.luminance() < 0.5f
    BoxWithConstraints(modifier.fillMaxSize()) {
        // 各行高度随缩放变化，空白与分割线保持固定高度。
        val spacers = grid.rows.mapIndexed { i, row ->
            (if (row.gapBefore && i > 0) GapHeight else 0.dp) + (if (row.dividerBefore && i > 0) DividerHeight else 0.dp)
        }
        val fixed = spacers.fold(0.dp) { sum, value -> sum + value }
        val available = maxHeight - HeaderHeight
        val natural = BaseRowHeight * grid.rows.size
        val minZoom = if (grid.rows.isEmpty() || natural.value <= 0f) 1f
        else ((available - fixed) / natural).coerceIn(0.4f, 1f)
        var rawScale by remember { mutableFloatStateOf(zoom) }
        val scale = rawScale.coerceIn(minZoom, MaxZoom)
        val currentScale by rememberUpdatedState(scale)
        val currentMin by rememberUpdatedState(minZoom)
        val swipe by rememberUpdatedState(onSwipeWeek)
        val commit by rememberUpdatedState(onZoomCommit)
        val rowHeight = BaseRowHeight * scale
        val tops = buildList {
            var y = 0.dp
            grid.rows.forEachIndexed { i, _ ->
                y += spacers[i]
                add(y)
                y += rowHeight
            }
        }
        val contentHeight = (tops.lastOrNull() ?: 0.dp) + (if (grid.rows.isEmpty()) BaseRowHeight * 3 else rowHeight) + 8.dp
        val columnWidth = ((maxWidth - PeriodWidth) / columns.size.coerceAtLeast(1)) * maxOf(1f, scale)
        val zoomed = scale > 1.001f
        val horizontal = rememberScrollState()
        val vertical = rememberScrollState()
        var dragOffset by remember { mutableFloatStateOf(0f) }

        val gestures = Modifier
            .pointerInput(Unit) {
                // 在 Initial 阶段截获双指手势，避免内层滚动同时响应；单指事件原样交给滚动与翻周。
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    var pinched = false
                    do {
                        val event = awaitPointerEvent(PointerEventPass.Initial)
                        if (event.changes.count { it.pressed } >= 2) {
                            val factor = event.calculateZoom()
                            if (factor != 1f) rawScale = (currentScale * factor).coerceIn(currentMin, MaxZoom)
                            pinched = true
                            event.changes.forEach { it.consume() }
                        }
                    } while (event.changes.any { it.pressed })
                    if (pinched) commit(currentScale)
                }
            }
            .then(
                if (zoomed) Modifier else Modifier.pointerInput(Unit) {
                    val threshold = 72.dp.toPx()
                    detectHorizontalDragGestures(
                        onDragEnd = {
                            if (abs(dragOffset) > threshold) swipe(if (dragOffset < 0) 1 else -1)
                            dragOffset = 0f
                        },
                        onDragCancel = { dragOffset = 0f },
                    ) { change, delta ->
                        dragOffset += delta
                        change.consume()
                    }
                },
            )

        Column(gestures.graphicsLayer { translationX = dragOffset * 0.35f }) {
            Row(Modifier.fillMaxWidth().height(HeaderHeight).background(MaterialTheme.colorScheme.surfaceContainer)) {
                Box(Modifier.width(PeriodWidth).fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text("${columns.firstOrNull()?.date?.monthValue ?: today.monthValue}月", style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Row(Modifier.horizontalScroll(horizontal, enabled = zoomed)) {
                    columns.forEach { column -> DayHeader(column, columnWidth) }
                }
            }
            Row(Modifier.verticalScroll(vertical)) {
                Box(Modifier.width(PeriodWidth).height(contentHeight)) {
                    grid.rows.forEachIndexed { i, row ->
                        Column(
                            Modifier.offset(y = tops[i]).width(PeriodWidth).height(rowHeight),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = androidx.compose.foundation.layout.Arrangement.Center,
                        ) {
                            if (!row.extra) Text(row.label, fontSize = 13.sp, fontWeight = FontWeight.Bold)
                            listOfNotNull(row.startTime, row.endTime).forEach {
                                Text(it, fontSize = 9.sp, lineHeight = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                        if (row.dividerBefore && i > 0) DividerLine(tops[i] - DividerHeight / 2 - (if (row.gapBefore) GapHeight / 2 else 0.dp), PeriodWidth)
                    }
                }
                Row(Modifier.horizontalScroll(horizontal, enabled = zoomed)) {
                    columns.forEach { column ->
                        DayColumn(column, grid.rows, tops, rowHeight, columnWidth, contentHeight, display, colorOf, dark) { block ->
                            onBlockClick(block, column.date)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun DayHeader(column: GridColumn, width: Dp) {
    val today = column.isToday
    Column(
        Modifier.width(width).fillMaxSize().padding(vertical = 3.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.Center,
    ) {
        val color = if (today) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
        Text(WeekNames[column.date.dayOfWeek.value % 7], fontSize = 12.sp, lineHeight = 14.sp, fontWeight = if (today) FontWeight.ExtraBold else FontWeight.SemiBold, color = color)
        Text("%02d/%02d".format(java.util.Locale.ROOT, column.date.monthValue, column.date.dayOfMonth), fontSize = 10.sp, lineHeight = 12.sp,
            color = if (today) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
        if (column.dayKind == Protocol.DAY_KIND_MAKEUP) {
            Text("调休", fontSize = 9.sp, lineHeight = 11.sp, color = MaterialTheme.colorScheme.tertiary, fontWeight = FontWeight.Bold)
        }
    }
}

@Composable
private fun DayColumn(
    column: GridColumn,
    rows: List<GridRow>,
    tops: List<Dp>,
    rowHeight: Dp,
    width: Dp,
    height: Dp,
    display: ScheduleGridDisplay,
    colorOf: (String) -> String,
    dark: Boolean,
    onClick: (GridBlock) -> Unit,
) {
    val tint = if (column.isToday) MaterialTheme.colorScheme.primary.copy(alpha = 0.05f) else Color.Transparent
    Box(Modifier.width(width).height(height).background(tint).padding(horizontal = 2.dp)) {
        rows.forEachIndexed { i, row ->
            if (row.dividerBefore && i > 0) DividerLine(tops[i] - DividerHeight / 2 - (if (row.gapBefore) GapHeight / 2 else 0.dp), width)
        }
        val overlay = when {
            !column.hasData -> "暂无课表数据"
            column.dayKind == Protocol.DAY_KIND_HOLIDAY -> listOfNotNull("放假", column.holidayName?.takeIf(String::isNotBlank)).joinToString(" · ")
            else -> null
        }
        if (overlay != null) {
            val holiday = column.hasData
            Box(
                Modifier.fillMaxSize().padding(vertical = 2.dp).clip(RoundedCornerShape(12.dp))
                    .background((if (holiday) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant).copy(alpha = 0.07f)),
                contentAlignment = Alignment.Center,
            ) {
                // 竖排：每个字一行。
                Text(overlay.toList().joinToString("\n"), fontSize = 12.sp, lineHeight = 15.sp, textAlign = TextAlign.Center,
                    color = if (holiday) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        val laneGap = 2.dp
        column.blocks.forEach { block ->
            val top = tops[block.rowStart]
            val bottom = tops[block.rowStart + block.rowSpan - 1] + rowHeight
            val laneWidth = (width - 4.dp - laneGap * (block.laneCount - 1)) / block.laneCount
            CourseBlock(
                block = block,
                display = display,
                base = colorOf(block.entry.course.subject),
                dark = dark,
                modifier = Modifier
                    .offset(x = (laneWidth + laneGap) * block.lane, y = top + 2.dp)
                    .size(laneWidth, bottom - top - 4.dp),
                onClick = { onClick(block) },
            )
        }
    }
}

@Composable
private fun CourseBlock(block: GridBlock, display: ScheduleGridDisplay, base: String, dark: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val course = block.entry.course
    val (background, foreground) = subjectColorPair(base, dark)
    val pending = course.adjustmentPending == true
    val shape = RoundedCornerShape(12.dp)
    Box(
        modifier.clip(shape).background(background)
            .then(if (pending) Modifier.drawBehind {
                drawRoundRect(
                    color = foreground, cornerRadius = CornerRadius(12.dp.toPx()),
                    style = Stroke(width = 1.5.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(6.dp.toPx(), 4.dp.toPx()))),
                )
            } else Modifier)
            .clickable(onClick = onClick)
            .padding(horizontal = 5.dp, vertical = 5.dp),
    ) {
        Column {
            Text(course.subject, color = foreground, fontSize = 12.sp, lineHeight = 14.sp, fontWeight = FontWeight.Bold,
                maxLines = 3, overflow = TextOverflow.Ellipsis)
            val details = buildList {
                if (display.showLocation) course.location?.takeIf(String::isNotBlank)?.let(::add)
                if (display.mine) {
                    if (display.showClass && !block.entry.isPersonal) block.entry.className?.takeIf(String::isNotBlank)?.let(::add)
                } else if (display.showTeacher) course.teacher?.takeIf(String::isNotBlank)?.let(::add)
            }
            details.forEach {
                Text(it, color = foreground.copy(alpha = 0.86f), fontSize = 10.sp, lineHeight = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
        if (course.adjustmentId != null) {
            Text(
                "换",
                Modifier.align(Alignment.TopEnd).clip(RoundedCornerShape(5.dp)).background(foreground).padding(horizontal = 3.dp),
                color = background, fontSize = 9.sp, fontWeight = FontWeight.ExtraBold,
            )
        }
    }
}

@Composable
private fun DividerLine(y: Dp, width: Dp) {
    HorizontalDivider(
        Modifier.offset(y = y).width(width),
        thickness = 1.dp,
        color = MaterialTheme.colorScheme.outlineVariant,
    )
}

private val WeekNames = listOf("周日", "周一", "周二", "周三", "周四", "周五", "周六")

/** 学科色块的底色与文字色（按浅色/深色模式派生）。 */
fun subjectColorPair(base: String, dark: Boolean): Pair<Color, Color> {
    val (background, foreground) = SubjectColors.derive(base, dark)
    return hexColor(background) to hexColor(foreground)
}

fun hexColor(hex: String): Color = Color(android.graphics.Color.parseColor(hex))

/** 日期所在周的周一。 */
internal fun mondayOf(date: LocalDate): LocalDate = date.minusDays((date.dayOfWeek.value - 1).toLong())

/** 表格的 7 列：超出可浏览范围（minDate–maxDate）的日期显示“暂无课表数据”；数据还没读到时全部为空列。 */
private fun weekColumns(
    weekStart: LocalDate, minDate: String?, maxDate: String?, entries: (LocalDate) -> Triple<String?, String?, List<GridEntry>>,
): List<GridDay> {
    val min = minDate?.let(::parseScheduleDate)
    val max = maxDate?.let(::parseScheduleDate)
    return (0L until 7L).map { offset ->
        val date = weekStart.plusDays(offset)
        val (kind, holiday, list) = entries(date)
        GridDay(date, list, kind, holiday, (min == null || !date.isBefore(min)) && (max == null || !date.isAfter(max)))
    }
}

internal fun classGridDays(week: ScheduleWeekResponse?, weekStart: LocalDate): List<GridDay> =
    weekColumns(weekStart, week?.minDate, week?.maxDate) { date ->
        val day = week?.days?.firstOrNull { it.date == date.toString() }
        Triple(day?.dayKind, day?.holidayName, if (day?.enabled == true) day.courses.filter { it.enabled }.map { GridEntry(it) } else emptyList())
    }

internal fun myGridDays(week: MyScheduleWeekResponse?, weekStart: LocalDate): List<GridDay> =
    weekColumns(weekStart, week?.minDate, week?.maxDate) { date ->
        val day = week?.days?.firstOrNull { it.date == date.toString() }
        Triple(day?.dayKind, day?.holidayName, day?.items.orEmpty().flatMap { item ->
            item.courses.filter { it.enabled }.map { GridEntry(it, item.classId.takeUnless { item.isPersonal }, item.className) }
        })
    }
