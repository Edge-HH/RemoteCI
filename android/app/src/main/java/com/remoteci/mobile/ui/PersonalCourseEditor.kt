package com.remoteci.mobile.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Keyboard
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.PersonalCourseRequest
import com.remoteci.mobile.data.PersonalCourseView
import com.remoteci.mobile.data.SemesterSettingsView
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter

/** 周次选项，与档案课表 TimeRule 的 WeekCountDiv / WeekCountDivTotal 对应。 */
internal data class RotationOption(val div: Int, val total: Int, val label: String)

internal val CourseRotationOptions = listOf(
    RotationOption(0, 2, "每周"), RotationOption(1, 2, "单周"), RotationOption(2, 2, "双周"),
    RotationOption(1, 3, "三周轮换 · 第 1 周"), RotationOption(2, 3, "三周轮换 · 第 2 周"), RotationOption(3, 3, "三周轮换 · 第 3 周"),
    RotationOption(1, 4, "四周轮换 · 第 1 周"), RotationOption(2, 4, "四周轮换 · 第 2 周"),
    RotationOption(3, 4, "四周轮换 · 第 3 周"), RotationOption(4, 4, "四周轮换 · 第 4 周"),
)

/** 按周一到周日排列；值为服务端的 weekDay（0=周日）。 */
internal val CourseWeekDays = listOf(1 to "一", 2 to "二", 3 to "三", 4 to "四", 5 to "五", 6 to "六", 0 to "日")

private val clock = DateTimeFormatter.ofPattern("HH:mm")

/** 编辑框中的课程内容；时间用 LocalTime，避免手输字符串。 */
internal data class CourseForm(
    val subject: String,
    val location: String,
    val weekDay: Int,
    val start: LocalTime,
    val end: LocalTime,
    val rotation: RotationOption,
    val reminder: String,
    val notify: Boolean,
    val offsetMinutes: Int,
)

internal fun courseFormOf(course: PersonalCourseView?, now: ZonedDateTime = ZonedDateTime.now()): CourseForm {
    val currentOffset = now.offset.totalSeconds / 60
    if (course == null) return CourseForm(
        subject = "", location = "", weekDay = now.dayOfWeek.value % 7,
        start = LocalTime.of(8, 0), end = LocalTime.of(8, 45), rotation = CourseRotationOptions[0],
        reminder = "10", notify = true, offsetMinutes = currentOffset,
    )
    // 旧服务端只返回 startsAt/endsAt，按其中的日期与时刻推算星期和时段。
    val startsAt = runCatching { OffsetDateTime.parse(course.startsAt) }.getOrNull()
    val endsAt = runCatching { OffsetDateTime.parse(course.endsAt) }.getOrNull()
    fun time(value: String?, fallback: OffsetDateTime?, default: LocalTime) =
        value?.let { runCatching { LocalTime.parse(it, clock) }.getOrNull() } ?: fallback?.toLocalTime() ?: default
    return CourseForm(
        subject = course.subject, location = course.location,
        weekDay = course.weekDay ?: startsAt?.dayOfWeek?.value?.rem(7) ?: (now.dayOfWeek.value % 7),
        start = time(course.startTime, startsAt, LocalTime.of(8, 0)),
        end = time(course.endTime, endsAt, LocalTime.of(8, 45)),
        rotation = CourseRotationOptions.firstOrNull { it.div == course.weekCountDiv && (it.div == 0 || it.total == course.weekCountDivTotal) }
            ?: CourseRotationOptions[0],
        reminder = course.reminderMinutes?.toString().orEmpty(), notify = course.notifyAtStart,
        offsetMinutes = course.timeZoneOffsetMinutes ?: startsAt?.offset?.totalSeconds?.div(60) ?: currentOffset,
    )
}

internal fun CourseForm.toRequest(): Result<PersonalCourseRequest> = runCatching {
    require(subject.isNotBlank()) { "请输入课程名称" }
    require(end.isAfter(start)) { "结束时间需晚于开始时间" }
    val minutes = reminder.trim()
    require(minutes.isEmpty() || (minutes.toIntOrNull() ?: 0) in 1..10080) { "提前提醒为 1–10080 分钟，留空关闭" }
    PersonalCourseRequest(
        subject = subject.trim(), location = location.trim(), weekDay = weekDay,
        startTime = start.format(clock), endTime = end.format(clock),
        weekCountDiv = rotation.div, weekCountDivTotal = rotation.total, timeZoneOffsetMinutes = offsetMinutes,
        reminderMinutes = minutes.toIntOrNull(), notifyAtStart = notify,
    )
}

/** 学期说明：管理员未设置学期时只能选择“每周”。 */
internal fun semesterHint(semester: SemesterSettingsView?): String = when {
    semester == null -> "无法读取学期设置，单双周以服务端为准。"
    semester.startDate == null -> "管理员尚未设置学期开始日期，暂只能选择“每周”。"
    semester.currentWeek != null -> "本周为第 ${semester.currentWeek} 教学周（${if (semester.currentWeek % 2 == 1) "单周" else "双周"}）。"
    else -> "学期自 ${semester.startDate} 所在周开始，尚未开学。"
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PersonalCourseEditor(course: PersonalCourseView?, onDismiss: () -> Unit, onSaved: () -> Unit) {
    val scope = rememberCoroutineScope()
    var form by remember(course?.id) { mutableStateOf(courseFormOf(course)) }
    var semester by remember { mutableStateOf<SemesterSettingsView?>(null) }
    var semesterLoaded by remember { mutableStateOf(false) }
    var picking by remember { mutableStateOf<Boolean?>(null) } // true=开始，false=结束
    var rotationMenu by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) {
        semester = runCatching { AdminApi.semester() }.getOrNull()
        semesterLoaded = true
    }
    // 读不到学期设置（例如旧服务端）时不限制选项，由服务端校验。
    val rotationAllowed = !semesterLoaded || semester == null || semester?.startDate != null

    picking?.let { isStart ->
        CourseTimePickerDialog(
            title = if (isStart) "开始时间" else "结束时间",
            initial = if (isStart) form.start else form.end,
            onDismiss = { picking = null },
            onConfirm = { time ->
                picking = null
                form = if (isStart) {
                    // 调整开始时间时保持原课长，结束时间不超过当天。
                    val length = java.time.Duration.between(form.start, form.end).takeIf { !it.isNegative && !it.isZero }
                        ?: java.time.Duration.ofMinutes(45)
                    val end = time.plus(length).takeIf { it.isAfter(time) } ?: LocalTime.of(23, 59)
                    form.copy(start = time, end = end)
                } else form.copy(end = time)
            },
        )
    }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(if (course == null) "添加自定义课程" else "修改自定义课程") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("仅自己账号可见，自动同步到所有设备。", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(form.subject, { form = form.copy(subject = it) }, label = { Text("课程名称") },
                    singleLine = true, enabled = !busy, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(form.location, { form = form.copy(location = it) }, label = { Text("地点（可选）") },
                    singleLine = true, enabled = !busy, modifier = Modifier.fillMaxWidth())

                Text("每周", style = MaterialTheme.typography.labelLarge)
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    CourseWeekDays.forEachIndexed { index, (day, label) ->
                        SegmentedButton(
                            selected = form.weekDay == day,
                            onClick = { form = form.copy(weekDay = day) },
                            shape = SegmentedButtonDefaults.itemShape(index, CourseWeekDays.size),
                            enabled = !busy,
                            icon = {},
                            contentPadding = PaddingValues(0.dp),
                        ) { Text(label) }
                    }
                }

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TimeField("开始", form.start, !busy, Modifier.weight(1f)) { picking = true }
                    TimeField("结束", form.end, !busy, Modifier.weight(1f)) { picking = false }
                }

                ExposedDropdownMenuBox(expanded = rotationMenu, onExpandedChange = { if (!busy) rotationMenu = it }) {
                    OutlinedTextField(
                        value = form.rotation.label, onValueChange = {}, readOnly = true, enabled = !busy,
                        label = { Text("周次") },
                        trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(rotationMenu) },
                        supportingText = { Text(semesterHint(semester).takeIf { semesterLoaded }.orEmpty()) },
                        modifier = Modifier.fillMaxWidth().menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
                    )
                    ExposedDropdownMenu(expanded = rotationMenu, onDismissRequest = { rotationMenu = false }) {
                        CourseRotationOptions.forEach { option ->
                            DropdownMenuItem(
                                text = { Text(option.label) },
                                enabled = option.div == 0 || rotationAllowed,
                                onClick = { form = form.copy(rotation = option); rotationMenu = false },
                            )
                        }
                    }
                }

                OutlinedTextField(form.reminder, { value -> form = form.copy(reminder = value.filter(Char::isDigit).take(5)) },
                    label = { Text("提前提醒（分钟）") }, supportingText = { Text("留空关闭课前提醒") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    singleLine = true, enabled = !busy, modifier = Modifier.fillMaxWidth())
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(form.notify, { form = form.copy(notify = it) }, enabled = !busy); Text("上课时提醒")
                }
                course?.date?.let { date ->
                    Text("这是 $date 的单次课程，保存后将改为按所选星期每周重复。",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary)
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (course != null) TextButton(enabled = !busy, onClick = {
                    busy = true
                    scope.launch {
                        runCatching { AdminApi.deletePersonalCourse(course.id) }
                            .onSuccess { onSaved() }.onFailure { error = it.message; busy = false }
                    }
                }) { Text("删除这节课", color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                val body = form.toRequest().getOrElse { error = it.message; return@TextButton }
                busy = true
                scope.launch {
                    runCatching { AdminApi.savePersonalCourse(course?.id, body) }
                        .onSuccess { onSaved() }.onFailure { error = it.message; busy = false }
                }
            }) { Text(if (busy) "保存中…" else "保存") }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text("取消") } },
    )
}

/** 只读的时间框，点击后弹出时钟选择。 */
@Composable
private fun TimeField(label: String, value: LocalTime, enabled: Boolean, modifier: Modifier, onClick: () -> Unit) {
    OutlinedCard(onClick = onClick, enabled = enabled, modifier = modifier) {
        Column(Modifier.padding(horizontal = 16.dp, vertical = 10.dp)) {
            Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(value.format(clock), style = MaterialTheme.typography.headlineSmall)
        }
    }
}

/** Material 3 时钟选择器，可切换为键盘输入。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CourseTimePickerDialog(title: String, initial: LocalTime, onDismiss: () -> Unit, onConfirm: (LocalTime) -> Unit) {
    val state = rememberTimePickerState(initial.hour, initial.minute, is24Hour = true)
    var keyboard by rememberSaveable { mutableStateOf(false) }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(shape = MaterialTheme.shapes.extraLarge, tonalElevation = 6.dp, modifier = Modifier.padding(24.dp).widthIn(max = 360.dp)) {
            Column(Modifier.padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(title, style = MaterialTheme.typography.labelLarge, modifier = Modifier.fillMaxWidth().padding(bottom = 20.dp))
                if (keyboard) TimeInput(state) else TimePicker(state)
                Row(Modifier.fillMaxWidth().padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { keyboard = !keyboard }) {
                        Icon(if (keyboard) Icons.Rounded.Schedule else Icons.Rounded.Keyboard,
                            contentDescription = if (keyboard) "切换为时钟" else "切换为键盘输入")
                    }
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = onDismiss) { Text("取消") }
                    TextButton(onClick = { onConfirm(LocalTime.of(state.hour, state.minute)) }) { Text("确定") }
                }
            }
        }
    }
}

/** “我的日程”中所有自定义课程，含本周没有安排的单双周课程，点按进入编辑。 */
@Composable
internal fun PersonalCourseListDialog(
    courses: List<PersonalCourseView>,
    onDismiss: () -> Unit,
    onAdd: () -> Unit,
    onEdit: (PersonalCourseView) -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("自定义课程") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                if (courses.isEmpty()) Text("还没有自定义课程。", style = MaterialTheme.typography.bodyMedium)
                courses.forEach { course ->
                    ListItem(
                        headlineContent = { Text(course.subject) },
                        supportingContent = {
                            Text(listOf(course.summary.ifBlank { personalCourseFallbackSummary(course) }, course.location)
                                .filter { it.isNotBlank() }.joinToString(" · "))
                        },
                        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
                        trailingContent = { TextButton(onClick = { onEdit(course) }) { Text("修改") } },
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onAdd) { Text("添加") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("关闭") } },
    )
}

/** 旧服务端不返回 summary 时按下一次课的时间显示。 */
private fun personalCourseFallbackSummary(course: PersonalCourseView): String = runCatching {
    val start = OffsetDateTime.parse(course.startsAt).atZoneSameInstant(ZoneId.systemDefault())
    val end = OffsetDateTime.parse(course.endsAt).atZoneSameInstant(ZoneId.systemDefault())
    "${start.toLocalDate().takeIf { it != LocalDate.now() } ?: "今天"} ${start.format(clock)}–${end.format(clock)}"
}.getOrDefault("")
