package com.remoteci.mobile.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
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
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.AdminPanelSettings
import androidx.compose.material.icons.rounded.ArrowDropDown
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.ContentCopy
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Key
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.PersonAdd
import androidx.compose.material.icons.rounded.PersonOutline
import androidx.compose.material.icons.rounded.Smartphone
import androidx.compose.material.icons.rounded.Watch
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AccountRoleMutation
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.CreateUserRequest
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UpdateUserRequest
import com.remoteci.mobile.data.VisitorAccessState
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PeopleScreen(embedded: Boolean, snackbar: androidx.compose.material3.SnackbarHostState, onOpen: (Screen) -> Unit, onBack: (() -> Unit)?) {
    val user by ConnectionManagerUser()
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("人员与凭证", style = MaterialTheme.typography.titleLarge) },
            navigationIcon = { IconButton(onClick = { onBack?.invoke() }) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
            actions = {
                if (user?.has(Protocol.PERMISSION_MANAGE_USERS) == true) {
                    IconButton({ onOpen(Screen.Users) }) { Icon(Icons.Rounded.PersonAdd, contentDescription = "添加账号") }
                }
            },
        )
        if (user?.has(Protocol.PERMISSION_MANAGE_USERS) != true) {
            EmptyState("无权管理人员", "人员、角色和凭证由管理员或拥有人员管理权限的账号处理。")
        } else {
            val items = listOf(
                Triple("访客访问", "访客课表与自动进入设置", Icons.Rounded.PersonOutline),
                Triple("角色配置", "创建自定义角色，设置课表、通知、语音、控制与扩展权限", Icons.Rounded.AdminPanelSettings),
                Triple("账号管理", "新建、编辑、启停账号，分配角色与个人附加权限", Icons.Rounded.Group),
                Triple("密码与设备会话", "重置密码时可撤销所有手表登录", Icons.Rounded.Key),
                Triple("插件配对码", "添加、查看和撤销 ClassIsland 插件长期凭证", Icons.Rounded.Link),
            )
            ConnectedListCard(Modifier.padding(16.dp)) {
                items.forEachIndexed { index, (title, supporting, icon) ->
                    AppListItem(
                        title = title,
                        supporting = supporting,
                        leading = icon,
                        trailing = Icons.Rounded.ChevronRight,
                        index = index,
                        count = items.size,
                        onClick = {
                            when (index) {
                                0 -> onOpen(Screen.VisitorAccess)
                                1 -> onOpen(Screen.Roles)
                                2 -> onOpen(Screen.Users)
                                3 -> onOpen(Screen.Sessions)
                                4 -> onOpen(Screen.Pairing)
                            }
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun ConnectionManagerUser() = com.remoteci.mobile.data.ConnectionManager.currentUser.collectAsState()

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VisitorAccessScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    var visitor by remember { mutableStateOf<VisitorAccessState?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }

    fun load() {
        scope.launch {
            loadError = null
            runCatching { AdminApi.visitor() }
                .onSuccess { visitor = it }
                .onFailure { loadError = it.message ?: "读取访客设置失败" }
        }
    }

    fun save(next: VisitorAccessState) {
        if (saving) return
        scope.launch {
            saving = true
            runCatching { AdminApi.setVisitor(next) }
                .onSuccess {
                    visitor = it
                    snackbar.showSnackbar("访客设置已保存")
                }
                .onFailure { snackbar.showSnackbar(it.message ?: "保存访客设置失败") }
            saving = false
        }
    }

    LaunchedEffect(Unit) { load() }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("访客访问") },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
        )
        val state = visitor
        when {
            state != null -> ConnectedListCard(Modifier.padding(16.dp)) {
                AppListItem(
                    title = "启用访客访问",
                    supporting = "允许未登录用户只读查看访客课表",
                    leading = Icons.Rounded.PersonOutline,
                    switchChecked = state.enabled,
                    onSwitch = { save(state.copy(enabled = it)) },
                    index = 0,
                    count = 2,
                )
                AppListItem(
                    title = "自动进入访客页",
                    supporting = "仅在访客访问已启用时生效",
                    leading = Icons.Rounded.PersonOutline,
                    switchChecked = state.autoEnter,
                    onSwitch = { save(state.copy(autoEnter = it)) },
                    index = 1,
                    count = 2,
                )
            }
            loadError != null -> EmptyState("无法读取访客设置", loadError ?: "请稍后重试")
            else -> EmptyState("正在读取访客设置", "请稍候…")
        }
        if (loadError != null) {
            TextButton(onClick = ::load, modifier = Modifier.padding(horizontal = 16.dp)) { Text("重试") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RolesScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    var roles by remember { mutableStateOf(listOf<com.remoteci.mobile.data.AccountRoleInfo>()) }
    var name by remember { mutableStateOf("") }
    var grants by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { roles = runCatching { AdminApi.roles() }.getOrDefault(emptyList()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("角色配置") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (roles.isEmpty()) EmptyState("没有角色", "管理员可以创建自定义角色。")
        ConnectedListCard(Modifier.padding(16.dp)) {
            roles.forEachIndexed { index, role ->
                AppListItem(
                    title = role.name,
                    supporting = "${role.kind} · ${role.userCount} 个账号",
                    leading = Icons.Rounded.AdminPanelSettings,
                    index = index,
                    count = roles.size,
                    onClick = {
                        name = role.name
                        grants = role.defaultPermissions
                    },
                )
            }
        }
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(name, { name = it }, label = { Text("角色名称") }, modifier = Modifier.fillMaxWidth())
            PermissionEditor(grants) { grants = it }
            Button(onClick = {
                scope.launch {
                    runCatching { AdminApi.createRole(AccountRoleMutation(name, grants)) }
                        .onSuccess { roles = AdminApi.roles(); snackbar.showSnackbar("角色已保存") }
                        .onFailure { snackbar.showSnackbar(it.message ?: "保存失败") }
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = name.isNotBlank()) { Text("新建角色") }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UsersScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    val currentUser by ConnectionManagerUser()
    var users by remember { mutableStateOf(listOf<com.remoteci.mobile.data.UserListItem>()) }
    var roles by remember { mutableStateOf(listOf<com.remoteci.mobile.data.AccountRoleInfo>()) }
    var username by remember { mutableStateOf("") }
    var displayName by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var grants by remember { mutableIntStateOf(0) }
    var selectedRoleId by remember { mutableStateOf<String?>(null) }
    var roleMenuExpanded by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<com.remoteci.mobile.data.UserListItem?>(null) }
    LaunchedEffect(Unit) {
        runCatching { AdminApi.users() }
            .onSuccess { users = it }
            .onFailure { snackbar.showSnackbar(it.message ?: "读取账号失败") }
        runCatching { AdminApi.roles() }
            .onSuccess { roles = it }
            .onFailure { snackbar.showSnackbar(it.message ?: "读取角色失败") }
        selectedRoleId = roles.firstOrNull { it.kind == "Student" }?.id
            ?: roles.firstOrNull { it.kind != "Administrator" }?.id
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("账号管理") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        if (users.isEmpty()) EmptyState("还没有账号", "创建后可分配角色和个人权限。")
        ConnectedListCard(Modifier.padding(16.dp)) {
            users.forEachIndexed { index, item ->
                AppListItem(
                    title = "${item.displayName} · ${item.roleName ?: if (item.role == Protocol.ROLE_ADMIN) "管理员" else "用户"}",
                    supporting = item.username + if (item.enabled) "" else " · 已停用",
                    leading = Icons.Rounded.Group,
                    index = index,
                    count = users.size,
                    onClick = { editing = item; displayName = item.displayName; grants = item.grantedPermissions },
                )
            }
        }
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(username, { username = it }, label = { Text("ID") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(displayName, { displayName = it }, label = { Text("显示名") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(password, { password = it }, label = { Text("密码") }, modifier = Modifier.fillMaxWidth())
            Text("角色", style = MaterialTheme.typography.titleSmall)
            val selectableRoles = roles.filter { currentUser?.isAdmin == true || it.kind != "Administrator" }
            Box {
                OutlinedButton(
                    onClick = { roleMenuExpanded = true },
                    modifier = Modifier.fillMaxWidth().height(56.dp),
                    enabled = selectableRoles.isNotEmpty(),
                ) {
                    Text(
                        selectableRoles.firstOrNull { it.id == selectedRoleId }?.name ?: "选择账号角色",
                        modifier = Modifier.weight(1f),
                    )
                    Icon(Icons.Rounded.ArrowDropDown, contentDescription = null)
                }
                DropdownMenu(
                    expanded = roleMenuExpanded,
                    onDismissRequest = { roleMenuExpanded = false },
                    modifier = Modifier.fillMaxWidth(.9f),
                ) {
                    selectableRoles.forEach { role ->
                        DropdownMenuItem(
                            text = { Text(role.name) },
                            onClick = {
                                selectedRoleId = role.id
                                roleMenuExpanded = false
                            },
                        )
                    }
                }
            }
            if (selectableRoles.isEmpty()) Text("没有可用角色，请先检查连接或在角色配置中创建角色。", color = MaterialTheme.colorScheme.error)
            PermissionEditor(grants) { grants = it }
            Button(onClick = {
                scope.launch {
                    runCatching {
                        val selectedRole = roles.firstOrNull { it.id == selectedRoleId }
                            ?: error("请选择账号角色")
                        AdminApi.createUser(
                            CreateUserRequest(
                                username.trim(),
                                displayName.trim(),
                                password,
                                if (selectedRole.kind == "Administrator") Protocol.ROLE_ADMIN else Protocol.ROLE_USER,
                                selectedRole.id,
                                grants,
                            ),
                        )
                    }.onSuccess {
                        users = AdminApi.users()
                        username = ""
                        displayName = ""
                        password = ""
                        grants = 0
                        snackbar.showSnackbar("账号已创建")
                    }
                        .onFailure { snackbar.showSnackbar(it.message ?: "创建失败") }
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = selectedRoleId != null && username.length >= 3 && displayName.isNotBlank() && password.length >= 8) { Text("新建账号") }
        }
    }
    editing?.let { item ->
        AlertDialog(
            onDismissRequest = { editing = null },
            title = { Text("编辑 ${item.displayName}") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(displayName, { displayName = it }, label = { Text("显示名") })
                    PermissionEditor(grants) { grants = it }
                }
            },
            confirmButton = {
                TextButton({
                    scope.launch {
                        runCatching {
                            AdminApi.updateUser(item.id, UpdateUserRequest(displayName, item.role, item.roleId, grants, item.enabled))
                        }.onSuccess { users = AdminApi.users(); editing = null }
                            .onFailure { snackbar.showSnackbar(it.message ?: "更新失败") }
                    }
                }) { Text("保存") }
            },
            dismissButton = { TextButton({ editing = null }) { Text("取消") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SessionsScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    var sessions by remember { mutableStateOf(listOf<com.remoteci.mobile.data.DeviceSessionSummary>()) }
    var current by remember { mutableStateOf("") }
    var next by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { sessions = runCatching { AdminApi.sessions() }.getOrDefault(emptyList()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("密码与设备") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(current, { current = it }, label = { Text("当前密码") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(next, { next = it }, label = { Text("新密码") }, modifier = Modifier.fillMaxWidth())
            Button(onClick = {
                scope.launch {
                    runCatching { AdminApi.changePassword(current, next) }
                        .onSuccess { snackbar.showSnackbar("密码已更新，其他设备登录将失效") }
                        .onFailure { snackbar.showSnackbar(it.message ?: "修改失败") }
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = current.isNotBlank() && next.length >= 8) { Text("修改密码") }
        }
        if (sessions.isEmpty()) EmptyState("没有其他登录设备", "当前账号的设备会话会显示在这里。")
        ConnectedListCard(Modifier.padding(16.dp)) {
            sessions.forEachIndexed { index, item ->
                AppListItem(
                    title = item.deviceName.ifBlank { "未命名设备" } + if (item.current) " · 本机" else "",
                    supporting = "最近使用 ${item.lastSeenAt ?: "未知"}",
                    leading = if (item.current || item.deviceName.contains("Android", ignoreCase = true)) Icons.Rounded.Smartphone else Icons.Rounded.Watch,
                    trailingText = "吊销",
                    index = index,
                    count = sessions.size,
                    onClick = {
                        scope.launch {
                            runCatching { AdminApi.revokeSession(item.id) }
                            sessions = runCatching { AdminApi.sessions() }.getOrDefault(sessions)
                        }
                    },
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairingScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var credentials by remember { mutableStateOf(listOf<com.remoteci.mobile.data.PluginCredentialInfo>()) }
    var code by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { credentials = runCatching { AdminApi.pluginCredentials() }.getOrDefault(emptyList()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("插件配对码") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        Button(onClick = {
            scope.launch {
                code = runCatching { AdminApi.pairingCode().pairCode }.getOrElse {
                    snackbar.showSnackbar(it.message ?: "无法生成配对码"); null
                }
            }
        }, modifier = Modifier.padding(16.dp).fillMaxWidth().height(56.dp)) { Text("生成配对码") }
        code?.let { value ->
            Row(
                modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                SelectionContainer(Modifier.weight(1f)) {
                    Text(value, style = MaterialTheme.typography.headlineSmall)
                }
                FilledTonalButton(onClick = {
                    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    clipboard.setPrimaryClip(ClipData.newPlainText("RemoteCI 插件配对码", value))
                    scope.launch { snackbar.showSnackbar("配对码已复制") }
                }) {
                    Icon(Icons.Rounded.ContentCopy, contentDescription = null)
                    Text("复制")
                }
            }
            Text("配对码只能使用一次，请立即粘贴到 ClassIsland 插件。", modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
        }
        if (credentials.isEmpty()) EmptyState("没有插件凭证", "生成配对码并在 ClassIsland 插件中使用后，会出现在这里。")
        ConnectedListCard(Modifier.padding(16.dp)) {
            credentials.forEachIndexed { index, item ->
                AppListItem(
                    title = item.name.ifBlank { "插件凭证" },
                    supporting = "最近 ${item.lastSeenAt ?: "尚未连接"}",
                    leading = Icons.Rounded.Link,
                    trailingText = "撤销",
                    index = index,
                    count = credentials.size,
                    onClick = {
                        scope.launch {
                            runCatching { AdminApi.revokePluginCredential(item.id) }
                            credentials = runCatching { AdminApi.pluginCredentials() }.getOrDefault(credentials)
                        }
                    },
                )
            }
        }
    }
}

@Composable
fun PermissionEditor(value: Int, onChange: (Int) -> Unit) {
    val flags = listOf(
        Protocol.PERMISSION_ACCESS_WEB_UI to "概览",
        Protocol.PERMISSION_MANAGE_USERS to "人员管理",
        Protocol.PERMISSION_SEND_NOTIFICATIONS to "通知",
        Protocol.PERMISSION_SEND_VOICE_MESSAGES to "语音",
        Protocol.PERMISSION_TEACHER_COMING to "老师来了",
        Protocol.PERMISSION_MANAGE_SCHEDULE to "换课",
        Protocol.PERMISSION_RUN_EXTENSIONS to "扩展",
        Protocol.PERMISSION_MAIN_MENU_CONTROL to "主界面",
        Protocol.PERMISSION_POWER_CONTROL to "电源",
    )
    Column {
        flags.forEach { (flag, label) ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(label)
                Switch(checked = value and flag == flag, onCheckedChange = { on ->
                    onChange(if (on) value or flag else value and flag.inv())
                })
            }
        }
    }
}
