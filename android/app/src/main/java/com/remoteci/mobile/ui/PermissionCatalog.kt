package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol

/** 权限清单中的一项：班级权限位或系统管理权限位。 */
internal data class PermissionItem(
    val label: String,
    val hint: String,
    val value: Int,
    val isManagement: Boolean = false,
) {
    /** 基础权限（查看课表与当前课程）：所有账号始终拥有，清单中只读。 */
    val isBase: Boolean get() = !isManagement && value == Protocol.PERMISSION_VIEW_CURRENT
}

/** 权限清单的一组；id 用来选图标，scope 为“班级内”或“全局”。 */
internal data class PermissionGroup(val id: String, val title: String, val scope: String, val items: List<PermissionItem>)

/** 一组勾选状态：班级权限与系统管理权限。 */
internal data class PermissionSelection(val permissions: Int = Protocol.PERMISSION_VIEW_CURRENT, val management: Int = 0) {
    fun has(item: PermissionItem): Boolean = item.isBase ||
        if (item.isManagement) management and item.value == item.value else permissions and item.value == item.value

    fun with(item: PermissionItem, checked: Boolean): PermissionSelection = when {
        item.isBase -> this
        item.isManagement -> copy(management = if (checked) management or item.value else management and item.value.inv())
        else -> copy(permissions = if (checked) permissions or item.value else permissions and item.value.inv())
    }

    fun withGroup(group: PermissionGroup, checked: Boolean): PermissionSelection =
        group.items.fold(this) { selection, item -> selection.with(item, checked) }

    /** 与基准（角色默认）不同的项：勾选了角色没有的为“已新增”，取消了角色已有的为“已收回”。 */
    fun adjustmentLabel(item: PermissionItem, baseline: PermissionSelection?): String? {
        if (baseline == null || item.isBase || has(item) == baseline.has(item)) return null
        return if (has(item)) "已新增" else "已收回"
    }

    /** 清单内容是否与基准一致（只比较清单中出现的权限位）。 */
    fun matches(baseline: PermissionSelection): Boolean =
        PermissionCatalog.items.all { has(it) == baseline.has(it) }

    companion object {
        fun ofRole(defaultPermissions: Int, defaultManagement: Int) =
            PermissionSelection(defaultPermissions or Protocol.PERMISSION_VIEW_CURRENT, defaultManagement)
    }
}

/** 账号相对角色默认权限的个人调整，与服务端 RolePermissions.Adjust 相同。 */
internal data class PermissionAdjustment(
    val granted: Int,
    val revoked: Int,
    val grantedManagement: Int,
    val revokedManagement: Int,
) {
    companion object {
        /**
         * 把勾选结果换算成相对角色默认的新增与收回；清单中不出现的隐藏权限位（“老师来了”）保持账号原有的调整。
         */
        fun between(
            roleDefaults: PermissionSelection,
            selected: PermissionSelection,
            existingGranted: Int = 0,
            existingRevoked: Int = 0,
        ): PermissionAdjustment {
            val defaults = roleDefaults.permissions and Protocol.PERMISSION_ASSIGNABLE
            val chosen = selected.permissions and Protocol.PERMISSION_ASSIGNABLE
            val hidden = PermissionCatalog.HIDDEN
            val defaultManagement = roleDefaults.management and Protocol.MANAGEMENT_ALL
            val chosenManagement = selected.management and Protocol.MANAGEMENT_ALL
            return PermissionAdjustment(
                granted = (chosen and defaults.inv() and hidden.inv()) or (existingGranted and hidden),
                revoked = (defaults and chosen.inv() and hidden.inv()) or (existingRevoked and hidden),
                grantedManagement = chosenManagement and defaultManagement.inv(),
                revokedManagement = defaultManagement and chosenManagement.inv(),
            )
        }
    }
}

/**
 * 角色与人员编辑器展示的完整权限目录，与 WebUI（服务端 PermissionCatalog）一致：每个可授予的权限出现一次。
 * “班级内”分组按账号在各班级的角色计算；“全局”分组按账号的全局角色计算。
 */
internal object PermissionCatalog {
    const val CLASS_SCOPE = "班级内"
    const val GLOBAL_SCOPE = "全局"

    /** 不在编辑器中出现、保存时按原值保留的权限位。 */
    const val HIDDEN = Protocol.PERMISSION_TEACHER_COMING

    const val ADMIN_ONLY_NOTE = "系统配置、服务端更新与备份，以及管理员账号本身，仍只有系统管理员可以操作。"

    private fun p(label: String, hint: String, value: Int) = PermissionItem(label, hint, value)
    private fun m(label: String, hint: String, value: Int) = PermissionItem(label, hint, value, isManagement = true)

    val groups: List<PermissionGroup> = listOf(
        PermissionGroup("schedule", "课表与换课", CLASS_SCOPE, listOf(
            p("查看课表与当前课程", "所有账号的基础权限，始终拥有", Protocol.PERMISSION_VIEW_CURRENT),
            p("直接换课与编辑课表", "直接换课，修改科目任课老师，删除直接换课的调课记录", Protocol.PERMISSION_MANAGE_SCHEDULE),
            p("发起换课申请", "向对方老师发起临时换课，对方审批后生效", Protocol.PERMISSION_REQUEST_SWAP),
            p("强制换课", "不经审批立即换课，对方老师可撤回", Protocol.PERMISSION_FORCE_SWAP),
            p("从教室刷新课表", "让在线插件立即重新上报本班课表", Protocol.PERMISSION_PULL_SCHEDULE),
            p("本班档案", "查看、编辑、下发与收集本班 ClassIsland 档案和时间表", Protocol.PERMISSION_MANAGE_CLASS_PROFILE),
        )),
        PermissionGroup("messages", "通知与消息", CLASS_SCOPE, listOf(
            p("发送通知", "向教室电脑发送文字通知", Protocol.PERMISSION_SEND_NOTIFICATIONS),
            p("清除通知", "清除教室电脑上正在显示的通知", Protocol.PERMISSION_CLEAR_NOTIFICATIONS),
            p("语音消息", "录制并在教室电脑上播放语音", Protocol.PERMISSION_SEND_VOICE_MESSAGES),
        )),
        PermissionGroup("device", "教室电脑", CLASS_SCOPE, listOf(
            p("关机、重启与睡眠", "远程控制教室电脑电源", Protocol.PERMISSION_POWER_CONTROL),
            p("音量与静音", "调节教室电脑音量或静音", Protocol.PERMISSION_VOLUME_CONTROL),
            p("显示或隐藏主界面", "切换 ClassIsland 主界面的显示状态", Protocol.PERMISSION_MAIN_MENU_CONTROL),
            p("主界面文件", "上传或获取 ClassIsland 主界面（组件）配置文件", Protocol.PERMISSION_MAIN_WINDOW_LAYOUT),
            p("屏幕截图", "获取教室电脑当前屏幕", Protocol.PERMISSION_CAPTURE_SCREEN),
            p("文件分发", "经服务端中转把文件发到教室电脑的桌面、下载或文档文件夹", Protocol.PERMISSION_SEND_FILES),
            p("远程终端", "在教室电脑上执行命令，属于高风险操作", Protocol.PERMISSION_REMOTE_TERMINAL),
        )),
        PermissionGroup("classisland", "ClassIsland 与插件", CLASS_SCOPE, listOf(
            p("使用扩展功能", "运行其他插件接入的远程功能（还受各扩展的开放设置限制）", Protocol.PERMISSION_RUN_EXTENSIONS),
            p("修改本班插件设置", "修改已开放给班级的扩展插件设置", Protocol.PERMISSION_CONFIGURE_EXTENSIONS),
            p("管理插件", "安装、卸载、启用或停用插件，下发插件包与插件管理策略", Protocol.PERMISSION_MANAGE_PLUGINS),
            p("升级软件", "升级 ClassIsland 与插件，刷新版本清单", Protocol.PERMISSION_UPGRADE_SOFTWARE),
            p("ClassIsland 程序控制", "远程启动、重启 ClassIsland，加入 ClassIsland 集控", Protocol.PERMISSION_CONTROL_CLASSISLAND),
            p("设备应用守护", "停用、卸载或立即更新教室电脑上的应用守护", Protocol.PERMISSION_MANAGE_DEVICE_GUARD),
        )),
        PermissionGroup("class", "班级", CLASS_SCOPE, listOf(
            p("班级名称与头像", "修改所在班级的班名，上传或清除班级头像", Protocol.PERMISSION_MANAGE_CLASS_INFO),
            p("电子班牌", "生成与吊销本班电子班牌链接", Protocol.PERMISSION_MANAGE_CLASS_BOARDS),
            p("本班操作记录与设备状态", "查看本班的命令操作记录和设备在线状态", Protocol.PERMISSION_VIEW_AUDIT_LOG),
            p("本班课时统计", "查看与导出本班课时与代课统计", Protocol.PERMISSION_VIEW_LESSON_STATS),
        )),
        PermissionGroup("account", "人员与账号", GLOBAL_SCOPE, listOf(
            p("概览页", "打开 WebUI 概览仪表盘与连接状态", Protocol.PERMISSION_ACCESS_WEB_UI),
            p("API 访问", "生成并使用 API Key 调用 REST 接口，按账号当前权限逐次鉴权", Protocol.PERMISSION_API_ACCESS),
            p("人员管理", "新建、编辑、停用、删除账号，重置密码，管理 API Key", Protocol.PERMISSION_MANAGE_USERS),
            m("角色配置", "创建、编辑、删除角色及其默认权限", Protocol.MANAGEMENT_MANAGE_ROLES),
            m("批量导入导出人员", "Excel 与文本批量导入、导出人员与成员关系", Protocol.MANAGEMENT_IMPORT_USERS),
        )),
        PermissionGroup("academic", "教务与班级管理", GLOBAL_SCOPE, listOf(
            m("班级管理", "建班、分组、成员、连接码、设备分配与访客开关", Protocol.MANAGEMENT_MANAGE_CLASSES),
            m("档案库", "管理模板与全部班级的档案、下发、收集与版本历史", Protocol.MANAGEMENT_MANAGE_PROFILE_LIBRARY),
            m("学期与调休", "设置学期、教学周与节假日调休", Protocol.MANAGEMENT_MANAGE_HOLIDAYS),
            m("查看全部操作记录", "查看全部班级与未分配设备的操作记录", Protocol.MANAGEMENT_VIEW_ALL_AUDIT_LOGS),
            m("查看全部课时统计", "查看与导出全部班级的课时统计", Protocol.MANAGEMENT_VIEW_ALL_LESSON_STATS),
        )),
        PermissionGroup("system", "设备与系统", GLOBAL_SCOPE, listOf(
            m("设备状态", "查看全部班级设备的在线、版本与告警", Protocol.MANAGEMENT_VIEW_DEVICES),
            m("批量控制", "向多个班级下发命令；每项操作仍需上面对应的权限", Protocol.MANAGEMENT_BATCH_CONTROL),
            m("自动化", "创建与运行自动化规则；规则以修改人的权限执行", Protocol.MANAGEMENT_MANAGE_AUTOMATION),
            m("应用守护策略", "设置全局与单设备的应用守护策略", Protocol.MANAGEMENT_MANAGE_APP_GUARD),
            m("插件批量下发", "跨班级下发插件设置，管理扩展调用策略与插件的班级开放", Protocol.MANAGEMENT_MANAGE_EXTENSION_SETTINGS),
            m("登录页与访客", "登录页外观、扫码登录地址与访客自动进入", Protocol.MANAGEMENT_MANAGE_LOGIN_PAGE),
            m("插件凭据与连接码", "生成配对码，吊销插件凭据，设置课表自动拉取间隔", Protocol.MANAGEMENT_MANAGE_PLUGIN_ACCESS),
        )),
    )

    val items: List<PermissionItem> get() = groups.flatMap { it.items }
}
