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
import androidx.compose.runtime.mutableStateMapOf
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
import com.remoteci.mobile.data.SwapCatalogDay
import com.remoteci.mobile.data.SwapRequestView
import com.remoteci.mobile.data.SwapSlot
import kotlinx.coroutines.launch
import java.time.LocalDate

/** 需要确认的换课冲突：列出冲突，确认后带确认标记重新提交。 */
private data class ConflictPrompt(val title: String, val text: String, val confirm: String, val retry: () -> Unit)

@Composable
private fun ConflictDialog(prompt: ConflictPrompt, onDismiss: () -> Unit) = AlertDialog(
    onDismissRequest = onDismiss,
    title = { Text(prompt.title) },
    text = { Text(prompt.text) },
    confirmButton = { TextButton({ onDismiss(); prompt.retry() }) { Text(prompt.confirm) } },
    dismissButton = { TextButton(onDismiss) { Text("取消") } },
)

/** 底栏“换课”：发起申请（可强制）、待我处理、我的申请。申请一律为临时换课，永久换课由班主任在课表页操作。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SwapRequestsScreen(snackbar: SnackbarHostState, showTopBar: Boolean = true) {
    var page by remember { mutableIntStateOf(0) }
    var catalog by remember { mutableStateOf<SwapCatalog?>(null) }
    var incoming by remember { mutableStateOf<List<SwapRequestView>>(emptyList()) }
    var outgoing by remember { mutableStateOf<List<SwapRequestView>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var reload by remember { mutableIntStateOf(0) }
    var conflict by remember { mutableStateOf<ConflictPrompt?>(null) }
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
    // 强制换课看过即处理：打开“待我处理”时上报，角标随之减少；本次列表仍高亮，方便看到是哪条。
    LaunchedEffect(page, incoming) {
        val unseen = incoming.filter { it.canRevoke && it.actionRequired }.map { it.id }
        if (page != 1 || unseen.isEmpty()) return@LaunchedEffect
        runCatching { AdminApi.markSwapsSeen(unseen) }
        ConnectionManager.refreshSwapInbox()
    }
    // 收到新的个人通知（对方提交、通过或撤回）时刷新列表。
    LaunchedEffect(Unit) { ConnectionManager.userNotifications.collect { reload++ } }

    fun act(success: String, onConflict: ((String) -> Unit)? = null, block: suspend () -> Unit) {
        scope.launch {
            runCatching { block() }
                .onSuccess { snackbar.showSnackbar(success); reload++ }
                .onFailure { error ->
                    val text = error.swapConflictText()
                    if (text != null && onConflict != null) onConflict(text) else snackbar.showSnackbar("失败：${error.message}")
                }
        }
    }

    fun approve(item: SwapRequestView, note: String?, acknowledge: Boolean = false) {
        act("已同意换课", onConflict = { text ->
            conflict = ConflictPrompt("确认冲突后通过？", text, "仍然通过") { approve(item, note, acknowledge = true) }
        }) { AdminApi.approveSwap(item.id, note, acknowledge) }
    }

    conflict?.let { prompt -> ConflictDialog(prompt) { conflict = null } }

    Column(Modifier.fillMaxSize()) {
        // 嵌在课表页时由课表页提供顶栏与分段，这里只显示三个子页。
        if (showTopBar) TopAppBar(title = { Text("换课", style = MaterialTheme.typography.titleLarge) })
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
                                Button(onClick = { approve(item, note.ifBlank { null }) }, modifier = Modifier.weight(1f)) { Text("通过") }
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
                        } else if (item.canRevert) RevertButton(item) { act("已撤回换课，课表已恢复") { AdminApi.revertSwap(item.id) } }
                    }
                }
                else -> if (outgoing.isEmpty()) EmptyState("你还没有发起过换课申请", "在“发起申请”中选择课程并填写理由。")
                else outgoing.forEach { item ->
                    SwapCard(item) {
                        if (item.canCancel) OutlinedButton(onClick = { act("已撤销换课申请") { AdminApi.cancelSwap(item.id) } }, modifier = Modifier.fillMaxWidth()) { Text("撤销申请") }
                        else if (item.canRevert) RevertButton(item) { act("已撤回换课，课表已恢复") { AdminApi.revertSwap(item.id) } }
                    }
                }
            }
        }
    }
}

/** 班主任或系统管理员撤回已生效的换课（含强制换课）。 */
@Composable
private fun RevertButton(item: SwapRequestView, onRevert: () -> Unit) {
    var confirm by remember(item.id) { mutableStateOf(false) }
    OutlinedButton(onClick = { confirm = true }, modifier = Modifier.fillMaxWidth()) { Text("撤回换课") }
    if (confirm) AlertDialog(
        onDismissRequest = { confirm = false },
        title = { Text("撤回这次换课？") },
        text = { Text("撤回后课表恢复原状，并通知申请人和对方老师。") },
        confirmButton = { TextButton({ confirm = false; onRevert() }) { Text("撤回") } },
        dismissButton = { TextButton({ confirm = false }) { Text("取消") } },
    )
}

@Composable
private fun SwapCard(item: SwapRequestView, actions: @Composable () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (item.actionRequired) MaterialTheme.colorScheme.secondaryContainer
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
            swapStatusNotes(item).forEach { note ->
                Text(note, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary)
            }
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
    var conflict by remember { mutableStateOf<ConflictPrompt?>(null) }
    // 各班各天的课程：目录自带 7 天，选到更远的日期时由 SlotPicker 读取后放进来。
    val days = remember(catalog) { mutableStateMapOf<String, SwapCatalogDay>().apply { putAll(catalog.seedDays()) } }

    if (catalog.classes.isEmpty()) {
        EmptyState("暂时没有可换的课表", "班级插件在线并同步课表后才能选择课程。")
        return
    }

    fun submit(force: Boolean, acknowledge: Boolean = false) {
        validateSwap(catalog, exchange, source, target, subject, reason, days)?.let { message ->
            scope.launch { snackbar.showSnackbar(message) }
            return
        }
        val targetPick = target!!
        submitting = true
        scope.launch {
            runCatching {
                AdminApi.createSwap(CreateSwapRequest(
                    mode = if (exchange) Swap.MODE_EXCHANGE else Swap.MODE_REPLACE,
                    source = if (exchange) source?.let { SwapSlot(it.classId, it.date, it.index!!) } else null,
                    target = SwapSlot(targetPick.classId, targetPick.date, targetPick.index!!),
                    subjectName = if (exchange) null else subject,
                    reason = reason.trim(),
                    force = force,
                    acknowledgeConflicts = acknowledge,
                ))
            }.onSuccess { onCreated(it) }
                .onFailure { error ->
                    // 任课冲突等需要确认：列出冲突，确认后带确认标记重新提交。
                    val text = error.swapConflictText()
                    if (text != null) conflict = ConflictPrompt("确认冲突后提交？", text, "仍然提交") { submit(force, acknowledge = true) }
                    else snackbar.showSnackbar("提交失败：${error.message}")
                }
            submitting = false
        }
    }

    conflict?.let { prompt -> ConflictDialog(prompt) { conflict = null } }

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
        SlotPicker(catalog, days, source) { source = it }
    }
    Text("目标课", style = MaterialTheme.typography.titleSmall)
    SlotPicker(catalog, days, target) { target = it }
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
            onClick = { if (validateSwap(catalog, exchange, source, target, subject, reason, days) == null) confirmForce = true else submit(true) },
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

/**
 * 班级 → 日期 → 节次 三级选择，数据来自换课目录（所有班级，含非本人任教的班）。
 * 日期用日期选择器在今天起 30 天内任选；目录里没有的日期按需读取当天课程。
 */
@Composable
private fun SlotPicker(
    catalog: SwapCatalog,
    days: MutableMap<String, SwapCatalogDay>,
    value: SwapPick?,
    onChange: (SwapPick) -> Unit,
) {
    val cls = catalog.classes.firstOrNull { it.classId == value?.classId }
    val range = cls?.dateRange()
    val day = value?.let { days[swapDayKey(it.classId, it.date)] }
    val course = catalog.course(value, days)
    var openClass by remember { mutableStateOf(false) }
    var openDate by remember { mutableStateOf(false) }
    var openCourse by remember { mutableStateOf(false) }
    var loadError by remember(value?.classId, value?.date) { mutableStateOf<String?>(null) }
    LaunchedEffect(value?.classId, value?.date) {
        val pick = value ?: return@LaunchedEffect
        val key = swapDayKey(pick.classId, pick.date)
        if (key in days) return@LaunchedEffect
        // 旧服务端没有按天读取的接口，目录里没有的日期就是没有课。
        if (cls?.minDate.isNullOrBlank()) {
            days[key] = SwapCatalogDay(pick.date)
            return@LaunchedEffect
        }
        runCatching { AdminApi.swapCatalogDay(pick.classId, pick.date) }
            .onSuccess { days[key] = it }
            .onFailure { loadError = it.message ?: "读取课程失败，请稍后再试" }
    }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box {
            OutlinedButton({ openClass = true }, Modifier.fillMaxWidth()) { Text("班级：${cls?.className ?: "选择班级"}") }
            DropdownMenu(openClass, { openClass = false }) {
                catalog.classes.forEach { item ->
                    DropdownMenuItem({ Text(item.className) }, {
                        openClass = false
                        val (min, max) = item.dateRange() ?: return@DropdownMenuItem
                        // 换班时尽量保留已选日期，超出该班范围时改选自己有课的第一天或该班第一天。
                        val date = value?.date?.takeIf { it in min..max } ?: item.myDates.firstOrNull() ?: min
                        onChange(SwapPick(item.classId, date))
                    })
                }
            }
        }
        if (cls != null && range != null) {
            OutlinedButton({ openDate = true }, Modifier.fillMaxWidth()) { Text("日期：${value?.date?.let(::swapDateLabel) ?: "选择日期"}") }
            if (openDate) BoundedDatePickerDialog(
                initial = value?.date?.let(::parseScheduleDate),
                min = parseScheduleDate(range.first) ?: LocalDate.now(),
                max = parseScheduleDate(range.second) ?: LocalDate.now(),
                onDismiss = { openDate = false },
                onPick = { onChange(SwapPick(cls.classId, it.toString())) },
            )
        }
        when {
            value == null || cls == null -> Unit
            loadError != null -> Text(loadError.orEmpty(), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            day == null -> Text("正在读取课程…", style = MaterialTheme.typography.bodySmall)
            day.courses.isEmpty() -> Text("这一天没有课，请选择其他日期。", style = MaterialTheme.typography.bodySmall)
            else -> Box {
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
}
