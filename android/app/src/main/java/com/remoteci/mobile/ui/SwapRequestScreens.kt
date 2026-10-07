package com.remoteci.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.CreateSwapRequest
import com.remoteci.mobile.data.Swap
import com.remoteci.mobile.data.SwapCatalog
import com.remoteci.mobile.data.SwapRequestView
import com.remoteci.mobile.data.SwapSlot
import kotlinx.coroutines.launch

/** 底栏“换课”：发起申请（可强制）、待我处理、我的申请。申请一律为临时换课，永久换课由班主任在课表页操作。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SwapRequestsScreen(snackbar: SnackbarHostState) {
    var page by remember { mutableIntStateOf(0) }
    var catalog by remember { mutableStateOf<SwapCatalog?>(null) }
    var incoming by remember { mutableStateOf<List<SwapRequestView>>(emptyList()) }
    var outgoing by remember { mutableStateOf<List<SwapRequestView>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var reload by remember { mutableIntStateOf(0) }
    val pending by ConnectionManager.pendingSwapCount.collectAsState()
    val scope = rememberCoroutineScope()

    LaunchedEffect(reload) {
        loading = true
        runCatching {
            catalog = AdminApi.swapCatalog()
            incoming = AdminApi.swapRequests("incoming")
            outgoing = AdminApi.swapRequests("outgoing")
        }.onFailure { snackbar.showSnackbar("加载换课信息失败：${it.message}") }
        ConnectionManager.refreshSwapInbox()
        loading = false
    }
    // 收到新的个人通知（对方提交、通过或撤回）时刷新列表。
    LaunchedEffect(Unit) { ConnectionManager.userNotifications.collect { reload++ } }

    fun act(success: String, block: suspend () -> Unit) {
        scope.launch {
            runCatching { block() }
                .onSuccess { snackbar.showSnackbar(success); reload++ }
                .onFailure { snackbar.showSnackbar("失败：${it.message}") }
        }
    }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("换课", style = MaterialTheme.typography.titleLarge) })
        PrimaryTabRow(selectedTabIndex = page) {
            Tab(selected = page == 0, onClick = { page = 0 }, text = { Text("发起申请") })
            Tab(selected = page == 1, onClick = { page = 1 }, text = { Text(if (pending > 0) "待我处理 · $pending" else "待我处理") })
            Tab(selected = page == 2, onClick = { page = 2 }, text = { Text("我的申请") })
        }
        if (loading && catalog == null) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            return@Column
        }
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            when (page) {
                0 -> catalog?.let { CreateSwapForm(it, snackbar) { created ->
                    act(when (created.status) {
                        Swap.STATUS_FORCED -> "已强制换课，并已通知对方老师"
                        Swap.STATUS_APPROVED -> "两节都是你的课，已直接完成临时换课"
                        else -> "换课申请已发送，等待对方老师处理"
                    }) { page = 2 }
                } }
                1 -> if (incoming.isEmpty()) EmptyState("还没有发给你的换课申请", "其他老师向你申请换课时会出现在这里。")
                else incoming.forEach { item ->
                    SwapCard(item) {
                        var note by remember(item.id) { mutableStateOf("") }
                        if (item.canDecide) {
                            OutlinedTextField(note, { note = it }, label = { Text("备注（可选）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Button(onClick = { act("已同意换课，课表已临时调整") { AdminApi.approveSwap(item.id, note.ifBlank { null }) } }, modifier = Modifier.weight(1f)) { Text("通过") }
                                OutlinedButton(onClick = { act("已拒绝换课申请") { AdminApi.rejectSwap(item.id, note.ifBlank { null }) } }, modifier = Modifier.weight(1f)) { Text("拒绝") }
                            }
                        }
                        if (item.canRevoke) {
                            var confirm by remember(item.id) { mutableStateOf(false) }
                            Button(
                                onClick = { confirm = true },
                                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                                modifier = Modifier.fillMaxWidth(),
                            ) { Text("撤回强制换课") }
                            if (confirm) AlertDialog(
                                onDismissRequest = { confirm = false },
                                title = { Text("撤回强制换课？") },
                                text = { Text("撤回后课表恢复原状，对方当天不能再强制换走这节课。") },
                                confirmButton = { TextButton({ confirm = false; act("已撤回强制换课，课表已恢复") { AdminApi.revokeSwap(item.id) } }) { Text("撤回") } },
                                dismissButton = { TextButton({ confirm = false }) { Text("取消") } },
                            )
                        }
                    }
                }
                else -> if (outgoing.isEmpty()) EmptyState("你还没有发起过换课申请", "在“发起申请”中选择课程并填写理由。")
                else outgoing.forEach { item ->
                    SwapCard(item) {
                        if (item.canCancel) OutlinedButton(onClick = { act("已撤销换课申请") { AdminApi.cancelSwap(item.id) } }, modifier = Modifier.fillMaxWidth()) { Text("撤销申请") }
                    }
                }
            }
        }
    }
}

@Composable
private fun SwapCard(item: SwapRequestView, actions: @Composable () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (item.canDecide || item.canRevoke) MaterialTheme.colorScheme.secondaryContainer
            else MaterialTheme.colorScheme.surfaceContainer,
        ),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text(
                    if (item.forced) "${item.requesterName} 强制换课" else item.requesterName.ifBlank { "换课申请" },
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    Swap.statusText(item.status),
                    style = MaterialTheme.typography.labelLarge,
                    color = when (item.status) {
                        Swap.STATUS_PENDING -> MaterialTheme.colorScheme.tertiary
                        Swap.STATUS_FORCED -> MaterialTheme.colorScheme.error
                        Swap.STATUS_APPROVED -> MaterialTheme.colorScheme.primary
                        else -> MaterialTheme.colorScheme.onSurfaceVariant
                    },
                )
            }
            Text(swapSummary(item), style = MaterialTheme.typography.bodyMedium)
            Text("理由：${item.reason}", style = MaterialTheme.typography.bodyMedium)
            item.decisionNote?.let { Text("备注：$it", style = MaterialTheme.typography.bodySmall) }
            if (item.approverNames.isNotEmpty())
                Text("审批人：${item.approverNames.joinToString("、")} · 编号 ${item.shortId}", style = MaterialTheme.typography.bodySmall)
            actions()
        }
    }
}

@Composable
private fun CreateSwapForm(catalog: SwapCatalog, snackbar: SnackbarHostState, onCreated: (SwapRequestView) -> Unit) {
    val scope = rememberCoroutineScope()
    var exchange by remember { mutableStateOf(true) }
    var source by remember { mutableStateOf(catalog.firstOwnPick()) }
    var target by remember { mutableStateOf<SwapPick?>(null) }
    var subject by remember { mutableStateOf(catalog.mySubjects.firstOrNull()) }
    var reason by remember { mutableStateOf("") }
    var confirmForce by remember { mutableStateOf(false) }
    var submitting by remember { mutableStateOf(false) }

    if (catalog.classes.isEmpty()) {
        EmptyState("暂时没有可换的课表", "班级插件在线并同步课表后才能选择课程。")
        return
    }

    fun submit(force: Boolean) {
        validateSwap(catalog, exchange, source, target, subject, reason)?.let { message ->
            scope.launch { snackbar.showSnackbar(message) }
            return
        }
        val targetPick = target!!
        submitting = true
        scope.launch {
            runCatching {
                AdminApi.createSwap(CreateSwapRequest(
                    mode = if (exchange) Swap.MODE_EXCHANGE else Swap.MODE_REPLACE,
                    source = if (exchange) source?.let { SwapSlot(it.classId, it.date, it.index) } else null,
                    target = SwapSlot(targetPick.classId, targetPick.date, targetPick.index),
                    subjectName = if (exchange) null else subject,
                    reason = reason.trim(),
                    force = force,
                ))
            }.onSuccess { onCreated(it) }
                .onFailure { snackbar.showSnackbar("提交失败：${it.message}") }
            submitting = false
        }
    }

    Row(horizontalArrangement = Arrangement.spacedBy(3.dp), modifier = Modifier.fillMaxWidth()) {
        val selected = ButtonDefaults.buttonColors()
        val idle = ButtonDefaults.outlinedButtonColors()
        Button({ exchange = true }, Modifier.weight(1f).height(52.dp), shape = connectedButtonShape(0, 2),
            colors = if (exchange) selected else idle) { Text("互换两节课") }
        Button({ exchange = false }, Modifier.weight(1f).height(52.dp), shape = connectedButtonShape(1, 2),
            colors = if (!exchange) selected else idle) { Text("替换为我的课") }
    }
    if (exchange) {
        Text("要换走的课", style = MaterialTheme.typography.titleSmall)
        Text("可以选其他班、其他老师的课，但两节课中至少有一节是你的课。", style = MaterialTheme.typography.bodySmall)
        SlotPicker(catalog, source) { source = it }
    }
    Text("目标课", style = MaterialTheme.typography.titleSmall)
    SlotPicker(catalog, target) { target = it }
    if (!exchange) {
        var open by remember { mutableStateOf(false) }
        Box {
            OutlinedButton({ open = true }, Modifier.fillMaxWidth()) { Text("换入学科：${subject ?: "选择你任教的学科"}") }
            DropdownMenu(open, { open = false }) {
                catalog.mySubjects.forEach { DropdownMenuItem({ Text(it) }, { subject = it; open = false }) }
            }
        }
        Text("目标课会临时改为你上的这门课，并计入你的个人日程。", style = MaterialTheme.typography.bodySmall)
    }
    OutlinedTextField(
        reason, { if (it.length <= 200) reason = it },
        label = { Text("换课理由") }, minLines = 2, modifier = Modifier.fillMaxWidth(),
    )
    Button({ submit(force = false) }, Modifier.fillMaxWidth().height(52.dp), enabled = !submitting) { Text("申请换课") }
    if (catalog.canForce) {
        Button(
            onClick = { if (validateSwap(catalog, exchange, source, target, subject, reason) == null) confirmForce = true else submit(true) },
            modifier = Modifier.fillMaxWidth().height(52.dp),
            enabled = !submitting,
            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
        ) { Text("强制换课") }
    }
    if (confirmForce) AlertDialog(
        onDismissRequest = { confirmForce = false },
        title = { Text("确认强制换课？") },
        text = { Text(Swap.FORCE_WARNING) },
        confirmButton = { TextButton({ confirmForce = false; submit(force = true) }) { Text("确定") } },
        dismissButton = { TextButton({ confirmForce = false }) { Text("取消") } },
    )
}

/** 班级 → 日期 → 节次 三级选择，数据来自换课目录（所有班级，含非本人任教的班）。 */
@Composable
private fun SlotPicker(catalog: SwapCatalog, value: SwapPick?, onChange: (SwapPick) -> Unit) {
    val cls = catalog.classes.firstOrNull { it.classId == value?.classId }
    val day = cls?.days?.firstOrNull { it.date == value?.date }
    val course = catalog.course(value)
    var openClass by remember { mutableStateOf(false) }
    var openDay by remember { mutableStateOf(false) }
    var openCourse by remember { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box {
            OutlinedButton({ openClass = true }, Modifier.fillMaxWidth()) { Text("班级：${cls?.className ?: "选择班级"}") }
            DropdownMenu(openClass, { openClass = false }) {
                catalog.classes.forEach { item ->
                    DropdownMenuItem({ Text(item.className) }, {
                        openClass = false
                        val firstDay = item.days.firstOrNull() ?: return@DropdownMenuItem
                        val first = firstDay.courses.firstOrNull() ?: return@DropdownMenuItem
                        onChange(SwapPick(item.classId, firstDay.date, first.index))
                    })
                }
            }
        }
        if (cls != null) Box {
            OutlinedButton({ openDay = true }, Modifier.fillMaxWidth()) { Text("日期：${day?.date?.let(::swapDateLabel) ?: "选择日期"}") }
            DropdownMenu(openDay, { openDay = false }) {
                cls.days.forEach { item ->
                    DropdownMenuItem({ Text(swapDateLabel(item.date)) }, {
                        openDay = false
                        item.courses.firstOrNull()?.let { onChange(SwapPick(cls.classId, item.date, it.index)) }
                    })
                }
            }
        }
        if (cls != null && day != null) Box {
            OutlinedButton({ openCourse = true }, Modifier.fillMaxWidth()) { Text("节次：${course?.let(::swapCourseLabel) ?: "选择节次"}") }
            DropdownMenu(openCourse, { openCourse = false }) {
                day.courses.forEach { item ->
                    DropdownMenuItem({ Text(swapCourseLabel(item)) }, {
                        openCourse = false
                        onChange(SwapPick(cls.classId, day.date, item.index))
                    })
                }
            }
        }
    }
}
