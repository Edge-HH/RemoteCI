package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ExtensionDefinition
import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.UserProfile

/**
 * 控制页的内置控制项，按 [group] 分组。哪些项作为“常用”在控制页顶部显示为磁贴，
 * 以及各项的排列顺序，由本机设置决定；扩展功能与它们一起排序和设为常用，见 [ControlEntry]。
 */
internal enum class ControlItem(
    val group: String,
    val title: String,
    val supporting: String,
    val permission: Int,
    val capability: String,
) {
    Notify("消息", "发送通知", "标题、正文、强调特效、音效和语音朗读",
        Protocol.PERMISSION_SEND_NOTIFICATIONS, Protocol.CAP_NOTIFICATION_SEND),
    Voice("消息", "发送语音", "最长 60 秒的语音消息",
        Protocol.PERMISSION_SEND_VOICE_MESSAGES, Protocol.CAP_VOICE_MESSAGE_SEND),
    ClearNotifications("消息", "清除提醒", "清除 ClassIsland 当前提醒与手表提示",
        Protocol.PERMISSION_CLEAR_NOTIFICATIONS, Protocol.CAP_NOTIFICATION_CLEAR),
    MainMenu("大屏", "主界面", "显示或隐藏 ClassIsland 主界面",
        Protocol.PERMISSION_MAIN_MENU_CONTROL, Protocol.CAP_MAIN_MENU_VISIBILITY),
    Screenshot("大屏", "屏幕截图", "查看教室电脑当前屏幕",
        Protocol.PERMISSION_CAPTURE_SCREEN, Protocol.CAP_SCREEN_CAPTURE),
    Volume("大屏", "音量", "音量调节和切换静音",
        Protocol.PERMISSION_VOLUME_CONTROL, Protocol.CAP_VOLUME_CONTROL),
    // 电源操作不可撤销，始终进入二级页确认，不做成一键操作。
    Power("大屏", "电源控制", "关机、睡眠或休眠",
        Protocol.PERMISSION_POWER_CONTROL, Protocol.CAP_POWER_CONTROL),
    Extensions("扩展", "扩展功能", "运行已注册扩展",
        Protocol.PERMISSION_RUN_EXTENSIONS, Protocol.CAP_EXTENSIONS_RUN),
}

/** 分组标题的固定顺序：按控制项的默认声明顺序，不随自定义排序变化。 */
internal val controlGroupNames: List<String> = ControlItem.entries.map { it.group }.distinct()

/** 未自定义“常用”时的默认磁贴，与引入自定义之前的“今天”页快捷控制一致。 */
internal val defaultControlFavorites: List<ControlItem> = listOf(
    ControlItem.Notify,
    ControlItem.ClearNotifications,
    ControlItem.MainMenu,
    ControlItem.Screenshot,
)

/** 扩展功能在保存的顺序与常用项里的 key 前缀，避免与内置控制项的枚举名冲突。 */
internal const val EXTENSION_KEY_PREFIX = "ext:"

/**
 * 控制页上可排序、可设为常用的一项：内置控制项，或某个已注册的扩展功能。
 * 扩展项保存为 `ext:<id>`，内置项保存为枚举名，两者共用一份顺序与常用项。
 */
internal sealed interface ControlEntry {
    val key: String
    val title: String
    val group: String

    data class Builtin(val item: ControlItem) : ControlEntry {
        override val key: String get() = item.name
        override val title: String get() = item.title
        override val group: String get() = item.group
    }

    data class Extension(val definition: ExtensionDefinition) : ControlEntry {
        val id: String get() = definition.id
        override val key: String get() = EXTENSION_KEY_PREFIX + id
        override val title: String get() = definition.displayName
        override val group: String get() = ControlItem.Extensions.group
    }
}

/**
 * 当前班级权限与插件能力下可用的控制项，保持枚举顺序。
 * 旧服务端没有细化后的独立权限位（音量、清除通知等），refined 为 false 时按合并的旧权限判断。
 */
internal fun visibleControlItems(classPermissions: Int, refined: Boolean, capabilities: Set<String>): List<ControlItem> =
    ControlItem.entries.filter {
        Protocol.hasPermission(classPermissions, it.permission, refined) && it.capability in capabilities
    }

/**
 * 扩展功能对当前账号是否可见：需要当前班级的运行扩展权限，并符合账号的开放清单与展示偏好，
 * 与扩展页使用同一条规则。
 */
internal fun extensionVisibleForClass(user: UserProfile?, classPermissions: Int, extension: ExtensionDefinition): Boolean {
    if ((classPermissions and Protocol.PERMISSION_RUN_EXTENSIONS) != Protocol.PERMISSION_RUN_EXTENSIONS) return false
    val allowed = user?.allowedExtensionIds
    val visible = user?.visibleExtensionIds
    return (allowed == null || extension.id in allowed) && (visible == null || extension.id in visible)
}

/** 当前可用的控制项（枚举顺序）与可见扩展功能（注册顺序）。 */
internal fun controlEntries(items: List<ControlItem>, extensions: List<ExtensionDefinition>): List<ControlEntry> =
    items.map { ControlEntry.Builtin(it) } + extensions.map { ControlEntry.Extension(it) }

/** 按保存的顺序排列；保存顺序里没有的项（新版本新增、新注册的扩展）按默认顺序追加到末尾。 */
internal fun orderedControlEntries(entries: List<ControlEntry>, savedOrder: List<String>): List<ControlEntry> {
    val rank = savedOrder.withIndex().associate { (index, key) -> key to index }
    val (known, unknown) = entries.partition { it.key in rank }
    return known.sortedBy { rank.getValue(it.key) } + unknown
}

/**
 * 常用项：[saved] 为 null 表示从未自定义，使用 [defaultControlFavorites]；
 * 保存为空列表表示用户取消了全部常用项。只保留当前可用的项，按常用项自己的顺序排列。
 */
internal fun favoriteControlEntries(entries: List<ControlEntry>, saved: List<String>?): List<ControlEntry> {
    val keys = saved ?: defaultControlFavorites.map { it.name }
    val byKey = entries.associateBy { it.key }
    return keys.mapNotNull { byKey[it] }
}

/** 控制页的显示布局：顶部是常用磁贴，下方是按分组列出的其余项。 */
internal data class ControlLayout(
    val favorites: List<ControlEntry>,
    val groups: List<ControlGroup>,
) {
    /** 分组里列出的项（不含常用磁贴）。 */
    val rest: List<ControlEntry> get() = groups.flatMap { it.items }

    val isEmpty: Boolean get() = favorites.isEmpty() && groups.isEmpty()
}

internal data class ControlGroup(val name: String, val items: List<ControlEntry>)

/**
 * 把当前可用的 [entries] 按 [savedOrder] 与 [savedFavorites] 排成控制页布局。
 * 常用项不再重复出现在分组里；分组顺序固定，组内按自定义顺序。
 */
internal fun controlLayout(
    entries: List<ControlEntry>,
    savedOrder: List<String>,
    savedFavorites: List<String>?,
): ControlLayout {
    val ordered = orderedControlEntries(entries, savedOrder)
    val favorites = favoriteControlEntries(ordered, savedFavorites)
    val favoriteKeys = favorites.map { it.key }.toSet()
    val groups = controlGroupNames.mapNotNull { name ->
        val groupEntries = ordered.filter { it.group == name && it.key !in favoriteKeys }
        if (groupEntries.isEmpty()) null else ControlGroup(name, groupEntries)
    }
    return ControlLayout(favorites, groups)
}

/** 切换常用：新增的常用项排在磁贴末尾；取消后回到分组里原来的位置。 */
internal fun toggledControlFavorite(favorites: List<ControlEntry>, entry: ControlEntry): List<ControlEntry> =
    if (favorites.any { it.key == entry.key }) favorites.filterNot { it.key == entry.key } else favorites + entry

/** 在列表中把 [entry] 前移或后移一位；不在列表里或已经在头/尾时返回 null。 */
internal fun <T> List<T>.movedWithin(entry: T, up: Boolean): List<T>? {
    val index = indexOf(entry)
    val target = if (up) index - 1 else index + 1
    if (index < 0 || target !in indices) return null
    return toMutableList().apply {
        this[index] = this@movedWithin[target]
        this[target] = entry
    }
}

/**
 * 在分组的排列里把 [entry] 与它前/后最近的同组、非常用项交换位置。
 * 常用项显示在顶部磁贴里，分组列表要跳过它们，否则上移一次看起来没有反应。
 */
internal fun movedWithinControlGroup(
    order: List<ControlEntry>,
    favoriteKeys: Set<String>,
    entry: ControlEntry,
    up: Boolean,
): List<ControlEntry>? {
    val index = order.indexOfFirst { it.key == entry.key }
    if (index < 0) return null
    val candidates = if (up) index - 1 downTo 0 else index + 1 until order.size
    val target = candidates.firstOrNull { order[it].group == entry.group && order[it].key !in favoriteKeys } ?: return null
    return order.toMutableList().apply {
        this[index] = order[target]
        this[target] = order[index]
    }
}
