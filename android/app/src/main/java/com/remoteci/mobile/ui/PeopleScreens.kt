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
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.AdminPanelSettings
import androidx.compose.material.icons.rounded.ArrowDropDown
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.ContentCopy
import androidx.compose.material.icons.rounded.Group
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
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.remoteci.mobile.data.AccountRoleInfo
import com.remoteci.mobile.data.AccountRoleMutation
import com.remoteci.mobile.data.AdminApi
import com.remoteci.mobile.data.CreateUserRequest
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UpdateUserRequest
import com.remoteci.mobile.data.UserListItem
import com.remoteci.mobile.data.VisitorAccessState
import kotlinx.coroutines.launch

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
    var roles by remember { mutableStateOf(listOf<AccountRoleInfo>()) }
    // null 为新建模式；否则编辑该角色。
    var editingRole by remember { mutableStateOf<AccountRoleInfo?>(null) }
    var name by remember { mutableStateOf("") }
    var selection by remember { mutableStateOf(PermissionSelection()) }
    var saving by remember { mutableStateOf(false) }

    fun startCreate() {
        editingRole = null
        name = ""
        selection = PermissionSelection()
    }

    fun startEdit(role: AccountRoleInfo) {
        editingRole = role
        name = role.name
        selection = role.defaults()
    }

    LaunchedEffect(Unit) { roles = runCatching { AdminApi.roles() }.getOrDefault(emptyList()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(
            title = { Text("角色配置") },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } },
            actions = {
                TextButton(onClick = ::startCreate, enabled = editingRole != null) {
                    Icon(Icons.Rounded.Add, contentDescription = null)
                    Text("新建角色")
                }
            },
        )
        if (roles.isEmpty()) EmptyState("没有角色", "管理员可以创建自定义角色。")
        ConnectedListCard(Modifier.padding(16.dp)) {
            roles.forEachIndexed { index, role ->
                AppListItem(
                    title = role.name + if (role.id == editingRole?.id) " · 编辑中" else "",
                    supporting = "${roleKindLabel(role)} · ${role.userCount} 个账号",
                    leading = Icons.Rounded.AdminPanelSettings,
                    index = index,
                    count = roles.size,
                    onClick = { startEdit(role) },
                )
            }
        }
        val role = editingRole
        val locked = role?.isAdministrator == true
        val builtIn = role != null && !role.isCustom
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(if (role == null) "新建角色" else "编辑「${role.name}」", style = MaterialTheme.typography.titleMedium)
            OutlinedTextField(
                name,
                { name = it },
                label = { Text("角色名称") },
                readOnly = builtIn,
                supportingText = if (builtIn) {
                    { Text("内置角色不能改名") }
                } else null,
                modifier = Modifier.fillMaxWidth(),
            )
            PermissionChecklist(
                selection = selection,
                onChange = { selection = it },
                title = "默认权限",
                description = if (locked) "管理员角色拥有全部权限，不能修改。"
                else "使用此角色的账号默认拥有勾选的权限；账号上的单独调整不受影响。",
                locked = locked,
            )
            Button(onClick = {
                if (saving) return@Button
                scope.launch {
                    saving = true
                    runCatching {
                        val chosen = selection.permissions and Protocol.PERMISSION_ASSIGNABLE
                        if (role == null) {
                            AdminApi.createRole(AccountRoleMutation(name.trim(), chosen, selection.management))
                        } else {
                            // “老师来了”不在清单中，保持角色原值。
                            val permissions = (chosen and PermissionCatalog.HIDDEN.inv()) or (role.defaultPermissions and PermissionCatalog.HIDDEN)
                            AdminApi.updateRole(role.id, AccountRoleMutation(name.trim(), permissions, selection.management))
                        }
                    }.onSuccess {
                        roles = runCatching { AdminApi.roles() }.getOrDefault(roles)
                        val updated = role?.let { saved -> roles.firstOrNull { it.id == saved.id } }
                        if (updated == null) startCreate() else startEdit(updated)
                        snackbar.showSnackbar("角色已保存")
                    }.onFailure { snackbar.showSnackbar(it.message ?: "保存失败") }
                    saving = false
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = !locked && !saving && name.isNotBlank()) {
                Text(if (role == null) "新建角色" else "保存角色")
            }
        }
    }
}

private fun roleKindLabel(role: AccountRoleInfo): String = when {
    role.isAdministrator -> "内置 · 全部权限"
    role.isCustom -> "自定义"
    else -> "内置"
}

/** 角色的默认勾选；管理员角色视为全部权限。 */
private fun AccountRoleInfo.defaults(): PermissionSelection =
    if (isAdministrator) PermissionSelection(Protocol.PERMISSION_ALL, Protocol.MANAGEMENT_ALL)
    else PermissionSelection.ofRole(defaultPermissions, defaultManagementPermissions)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UsersScreen(onBack: () -> Unit, snackbar: androidx.compose.material3.SnackbarHostState) {
    val scope = rememberCoroutineScope()
    val currentUser by ConnectionManagerUser()
    var users by remember { mutableStateOf(listOf<UserListItem>()) }
    var roles by remember { mutableStateOf(listOf<AccountRoleInfo>()) }
    var username by remember { mutableStateOf("") }
    var displayName by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var selection by remember { mutableStateOf(PermissionSelection()) }
    var selectedRoleId by remember { mutableStateOf<String?>(null) }
    var roleMenuExpanded by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<UserListItem?>(null) }
    var editName by remember { mutableStateOf("") }
    var editSelection by remember { mutableStateOf(PermissionSelection()) }

    fun selectRole(role: AccountRoleInfo?) {
        selectedRoleId = role?.id
        selection = role?.defaults() ?: PermissionSelection()
    }

    LaunchedEffect(Unit) {
        runCatching { AdminApi.users() }
            .onSuccess { users = it }
            .onFailure { snackbar.showSnackbar(it.message ?: "读取账号失败") }
        runCatching { AdminApi.roles() }
            .onSuccess { roles = it }
            .onFailure { snackbar.showSnackbar(it.message ?: "读取角色失败") }
        selectRole(roles.firstOrNull { it.kind == "Student" } ?: roles.firstOrNull { !it.isAdministrator })
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
                    onClick = {
                        editing = item
                        editName = item.displayName
                        editSelection = userSelection(item, roles.firstOrNull { it.id == item.roleId }?.defaults())
                    },
                )
            }
        }
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(username, { username = it }, label = { Text("ID") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(displayName, { displayName = it }, label = { Text("显示名") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(
                password,
                { password = it },
                label = { Text("初始密码") },
                supportingText = { Text("留空则账号首次登录时自行设置密码") },
                modifier = Modifier.fillMaxWidth(),
            )
            Text("角色", style = MaterialTheme.typography.titleSmall)
            val selectableRoles = roles.filter { currentUser?.isAdmin == true || !it.isAdministrator }
            val selectedRole = selectableRoles.firstOrNull { it.id == selectedRoleId }
            Box {
                OutlinedButton(
                    onClick = { roleMenuExpanded = true },
                    modifier = Modifier.fillMaxWidth().height(56.dp),
                    enabled = selectableRoles.isNotEmpty(),
                ) {
                    Text(selectedRole?.name ?: "选择账号角色", modifier = Modifier.weight(1f))
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
                                selectRole(role)
                                roleMenuExpanded = false
                            },
                        )
                    }
                }
            }
            if (selectableRoles.isEmpty()) Text("没有可用角色，请先检查连接或在角色配置中创建角色。", color = MaterialTheme.colorScheme.error)
            val createLocked = selectedRole?.isAdministrator == true
            PermissionChecklist(
                selection = selection,
                onChange = { selection = it },
                description = if (createLocked) "管理员拥有全部权限。" else "默认为所选角色的权限，可单独新增或收回。",
                modifier = Modifier.padding(top = 8.dp),
                baseline = selectedRole?.defaults(),
                locked = createLocked,
            )
            Button(onClick = {
                scope.launch {
                    runCatching {
                        val role = roles.firstOrNull { it.id == selectedRoleId } ?: error("请选择账号角色")
                        val adjustment = if (role.isAdministrator) PermissionAdjustment(0, 0, 0, 0)
                        else PermissionAdjustment.between(role.defaults(), selection)
                        AdminApi.createUser(
                            CreateUserRequest(
                                username.trim(),
                                displayName.trim(),
                                password,
                                if (role.isAdministrator) Protocol.ROLE_ADMIN else Protocol.ROLE_USER,
                                role.id,
                                adjustment.granted,
                                adjustment.revoked,
                                adjustment.grantedManagement,
                                adjustment.revokedManagement,
                            ),
                        )
                    }.onSuccess {
                        users = AdminApi.users()
                        username = ""
                        displayName = ""
                        password = ""
                        selectRole(roles.firstOrNull { it.id == selectedRoleId })
                        snackbar.showSnackbar("账号已创建")
                    }
                        .onFailure { snackbar.showSnackbar(it.message ?: "创建失败") }
                }
            }, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = selectedRoleId != null && username.length >= 3 && displayName.isNotBlank() && (password.isEmpty() || password.length >= 8)) { Text("新建账号") }
        }
    }
    editing?.let { item ->
        val role = roles.firstOrNull { it.id == item.roleId }
        val locked = item.role == Protocol.ROLE_ADMIN || role?.isAdministrator == true
        AlertDialog(
            onDismissRequest = { editing = null },
            title = { Text("编辑 ${item.displayName}") },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(editName, { editName = it }, label = { Text("显示名") }, modifier = Modifier.fillMaxWidth())
                    PermissionChecklist(
                        selection = editSelection,
                        onChange = { editSelection = it },
                        description = if (locked) "管理员拥有全部权限。"
                        else "角色「${role?.name ?: item.roleName ?: "未知"}」的默认权限，可单独新增或收回。",
                        baseline = role?.defaults(),
                        locked = locked || role == null,
                    )
                }
            },
            confirmButton = {
                TextButton({
                    scope.launch {
                        runCatching {
                            val request = if (locked || role == null) {
                                // 管理员或角色未知时只改显示名，权限调整保持原值。
                                UpdateUserRequest(editName, item.role, item.roleId, item.grantedPermissions, item.enabled)
                            } else {
                                val adjustment = PermissionAdjustment.between(
                                    role.defaults(), editSelection, item.grantedPermissions, item.revokedPermissions,
                                )
                                UpdateUserRequest(
                                    editName, item.role, item.roleId, adjustment.granted, item.enabled,
                                    adjustment.revoked, adjustment.grantedManagement, adjustment.revokedManagement,
                                )
                            }
                            AdminApi.updateUser(item.id, request)
                        }.onSuccess { users = AdminApi.users(); editing = null }
                            .onFailure { snackbar.showSnackbar(it.message ?: "更新失败") }
                    }
                }) { Text("保存") }
            },
            dismissButton = { TextButton({ editing = null }) { Text("取消") } },
        )
    }
}

/** 编辑账号时的初始勾选：服务端下发的有效权限，旧服务端缺少时退回角色默认。 */
internal fun userSelection(item: UserListItem, roleDefaults: PermissionSelection?): PermissionSelection = PermissionSelection(
    if (item.effectivePermissions != 0) item.effectivePermissions or Protocol.PERMISSION_VIEW_CURRENT
    else roleDefaults?.permissions ?: Protocol.PERMISSION_VIEW_CURRENT,
    item.effectiveManagementPermissions ?: roleDefaults?.management ?: 0,
)

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
    val classes by com.remoteci.mobile.data.ConnectionManager.classes.collectAsState()
    val currentClassId by com.remoteci.mobile.data.ConnectionManager.currentClassId.collectAsState()
    val currentClass = classes.firstOrNull { it.id == currentClassId }
    LaunchedEffect(Unit) { credentials = runCatching { AdminApi.pluginCredentials() }.getOrDefault(emptyList()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        TopAppBar(title = { Text("插件配对码") }, navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") } })
        // 服务端没有默认班级：配对码绑定当前班级，插件配对后归属该班。
        Text(
            currentClass?.let { "为当前班级「${it.name}」生成一次性配对码，可在左上角菜单切换班级。" }
                ?: "当前账号还没有可用的班级，请先在 WebUI 新建班级。",
            modifier = Modifier.padding(horizontal = 16.dp).padding(top = 8.dp),
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Button(onClick = {
            val classId = currentClass?.id ?: return@Button
            scope.launch {
                code = runCatching { AdminApi.pairingCode(classId).pairCode }.getOrElse {
                    snackbar.showSnackbar(it.message ?: "无法生成配对码"); null
                }
            }
        }, enabled = currentClass != null, modifier = Modifier.padding(16.dp).fillMaxWidth().height(56.dp)) { Text("生成配对码") }
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
