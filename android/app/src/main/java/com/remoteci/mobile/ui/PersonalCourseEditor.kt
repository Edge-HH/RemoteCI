package com.remoteci.mobile.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.PersonalCourseRequest
import com.remoteci.mobile.data.PersonalCourseView
import kotlinx.coroutines.launch
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
internal fun PersonalCourseEditor(course: PersonalCourseView?, onDismiss: () -> Unit, onSaved: () -> Unit) {
    val scope = rememberCoroutineScope()
    val format = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")
    fun local(value: String?) = value?.let {
        runCatching { OffsetDateTime.parse(it).atZoneSameInstant(ZoneId.systemDefault()).format(format) }.getOrNull()
    } ?: ""
    var subject by remember { mutableStateOf(course?.subject.orEmpty()) }
    var location by remember { mutableStateOf(course?.location.orEmpty()) }
    var start by remember { mutableStateOf(local(course?.startsAt)) }
    var end by remember { mutableStateOf(local(course?.endsAt)) }
    var minutes by remember { mutableStateOf(if (course == null) "10" else course.reminderMinutes?.toString().orEmpty()) }
    var notify by remember { mutableStateOf(course?.notifyAtStart ?: true) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(if (course == null) "添加自定义课程" else "修改自定义课程") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("仅自己账号可见，自动同步到所有设备。", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(subject, { subject = it }, label = { Text("课程名称") }, singleLine = true, enabled = !busy)
                OutlinedTextField(location, { location = it }, label = { Text("地点（可选）") }, singleLine = true, enabled = !busy)
                OutlinedTextField(start, { start = it }, label = { Text("开始：2026-10-17 08:00") }, singleLine = true, enabled = !busy)
                OutlinedTextField(end, { end = it }, label = { Text("结束：2026-10-17 08:45") }, singleLine = true, enabled = !busy)
                OutlinedTextField(minutes, { minutes = it }, label = { Text("提前分钟数，留空关闭") }, singleLine = true, enabled = !busy)
                Row { Checkbox(notify, { notify = it }, enabled = !busy); Text("上课时提醒") }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (course != null) TextButton(enabled = !busy, onClick = {
                    busy = true
                    scope.launch {
                        runCatching { AdminApi.deletePersonalCourse(course.id) }
                            .onSuccess { onSaved() }.onFailure { error = it.message; busy = false }
                    }
                }) { Text("删除这节课") }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                val body = runCatching {
                    require(subject.isNotBlank()) { "请输入课程名称" }
                    require(minutes.isBlank() || (minutes.toIntOrNull() ?: 0) in 1..10080) { "提前提醒为 1–10080 分钟" }
                    val startsAt = LocalDateTime.parse(start.trim(), format).atZone(ZoneId.systemDefault()).toOffsetDateTime()
                    val endsAt = LocalDateTime.parse(end.trim(), format).atZone(ZoneId.systemDefault()).toOffsetDateTime()
                    require(endsAt.isAfter(startsAt)) { "结束时间需晚于开始时间" }
                    PersonalCourseRequest(subject.trim(), location.trim(), startsAt.toString(), endsAt.toString(), minutes.toIntOrNull(), notify)
                }.getOrElse { error = it.message ?: "时间格式应为 yyyy-MM-dd HH:mm"; return@TextButton }
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
