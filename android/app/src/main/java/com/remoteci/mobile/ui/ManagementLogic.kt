package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile

/**
 * “我的”页“管理”分组的管理项，与服务端对应接口的鉴权一致：
 * permission 是班级权限位（人员管理），management 是系统管理权限位，adminOnly 只对系统管理员开放。
 * 旧服务端（不下发 managementPermissions）沿用原规则：有“人员管理”即可，legacyAdminOnly 的项只对系统管理员开放。
 */
internal enum class ManagementItem(
    val title: String,
    val supporting: String,
    val permission: Int = 0,
    val management: Int = 0,
    val adminOnly: Boolean = false,
    val legacyAdminOnly: Boolean = adminOnly,
) {
    Users("账号管理", "新建、编辑、启停账号，按角色勾选并单独调整权限", permission = Protocol.PERMISSION_MANAGE_USERS),
    Roles("角色配置", "创建角色，按分组勾选角色的默认权限", management = Protocol.MANAGEMENT_MANAGE_ROLES),
    VisitorAccess("访客访问", "访客课表与自动进入设置", management = Protocol.MANAGEMENT_MANAGE_LOGIN_PAGE),
    Pairing("插件配对码", "添加、查看和撤销 ClassIsland 插件长期凭证", management = Protocol.MANAGEMENT_MANAGE_PLUGIN_ACCESS),
    ScheduleSettings("自动拉取课表", "在线插件自动刷新课表的周期，对全部班级生效",
        management = Protocol.MANAGEMENT_MANAGE_PLUGIN_ACCESS, legacyAdminOnly = true),
    ServerMaintenance("服务端维护", "服务端更新与备份", adminOnly = true),
}

/** 当前账号有权限使用的管理项；为空时“我的”页不显示“管理”分组。 */
internal fun visibleManagementItems(user: UserProfile?): List<ManagementItem> = when {
    user == null -> emptyList()
    user.isAdmin -> ManagementItem.entries.toList()
    user.managementPermissions == null -> ManagementItem.entries.filter {
        user.has(Protocol.PERMISSION_MANAGE_USERS) && !it.legacyAdminOnly
    }
    else -> ManagementItem.entries.filter {
        !it.adminOnly &&
            (it.permission == 0 || user.has(it.permission)) &&
            (it.management == 0 || user.hasManagement(it.management))
    }
}
