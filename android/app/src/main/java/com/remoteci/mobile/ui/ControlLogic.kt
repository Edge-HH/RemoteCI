package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol

/**
 * 控制页的控制项，“今天”页的快捷磁贴与控制页共用同一份可见性判断。
 * quick 的项在控制页顶部以磁贴显示（一键操作或高频表单），其余按 group 分组列在下方并进入二级页。
 * todayShortcut 的项同时出现在“今天”页的快捷控制行。
 */
internal enum class ControlItem(
    val group: String,
    val title: String,
    val supporting: String,
    val permission: Int,
    val capability: String,
    val quick: Boolean,
    val todayShortcut: Boolean,
) {
    Notify("消息", "发送通知", "标题、正文、强调特效、音效和语音朗读",
        Protocol.PERMISSION_SEND_NOTIFICATIONS, Protocol.CAP_NOTIFICATION_SEND, quick = true, todayShortcut = true),
    Voice("消息", "发送语音", "最长 60 秒的语音消息",
        Protocol.PERMISSION_SEND_VOICE_MESSAGES, Protocol.CAP_VOICE_MESSAGE_SEND, quick = true, todayShortcut = false),
    ClearNotifications("消息", "清除提醒", "清除 ClassIsland 当前提醒与手表提示",
        Protocol.PERMISSION_CLEAR_NOTIFICATIONS, Protocol.CAP_NOTIFICATION_CLEAR, quick = true, todayShortcut = true),
    MainMenu("大屏", "主界面", "显示或隐藏 ClassIsland 主界面",
        Protocol.PERMISSION_MAIN_MENU_CONTROL, Protocol.CAP_MAIN_MENU_VISIBILITY, quick = true, todayShortcut = true),
    Screenshot("大屏", "屏幕截图", "查看教室电脑当前屏幕",
        Protocol.PERMISSION_CAPTURE_SCREEN, Protocol.CAP_SCREEN_CAPTURE, quick = false, todayShortcut = true),
    Volume("大屏", "音量", "音量调节和切换静音",
        Protocol.PERMISSION_VOLUME_CONTROL, Protocol.CAP_VOLUME_CONTROL, quick = false, todayShortcut = false),
    // 电源操作不可撤销，始终进入二级页确认，不做成一键磁贴。
    Power("大屏", "电源控制", "关机、睡眠或休眠",
        Protocol.PERMISSION_POWER_CONTROL, Protocol.CAP_POWER_CONTROL, quick = false, todayShortcut = false),
    Extensions("扩展", "扩展功能", "运行已注册扩展",
        Protocol.PERMISSION_RUN_EXTENSIONS, Protocol.CAP_EXTENSIONS_RUN, quick = false, todayShortcut = false),
}

/**
 * 当前班级权限与插件能力下可用的控制项，保持枚举顺序。
 * 旧服务端没有细化后的独立权限位（音量、清除通知等），refined 为 false 时按合并的旧权限判断。
 */
internal fun visibleControlItems(classPermissions: Int, refined: Boolean, capabilities: Set<String>): List<ControlItem> =
    ControlItem.entries.filter {
        Protocol.hasPermission(classPermissions, it.permission, refined) && it.capability in capabilities
    }
