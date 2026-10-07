package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile

/** “管理”页的管理项；permission 与服务端对应接口的鉴权一致。 */
internal enum class ManagementItem(val title: String, val supporting: String, val permission: Int) {
    VisitorAccess("访客访问", "访客课表与自动进入设置", Protocol.PERMISSION_MANAGE_USERS),
    Roles("角色配置", "创建自定义角色，设置课表、通知、语音、控制与扩展权限", Protocol.PERMISSION_MANAGE_USERS),
    Users("账号管理", "新建、编辑、启停账号，分配角色与个人附加权限", Protocol.PERMISSION_MANAGE_USERS),
    Pairing("插件配对码", "添加、查看和撤销 ClassIsland 插件长期凭证", Protocol.PERMISSION_MANAGE_USERS),
}

/** 当前账号有权限使用的管理项；为空时底栏不显示“管理”页。 */
internal fun visibleManagementItems(user: UserProfile?): List<ManagementItem> =
    if (user == null) emptyList() else ManagementItem.entries.filter { user.has(it.permission) }
