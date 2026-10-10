package com.remoteci.mobile.data

import android.content.Context
import java.net.URI

data class WatchSettings(
    val username: String = "",
    val cloudConnectionEnabled: Boolean = true,
    val cloudServerUrl: String = "http://10.0.2.2:8080",
    val lanConnectionEnabled: Boolean = true,
    val lanHost: String = "",
    val lanHostCandidates: List<String> = emptyList(),
    val lanPort: Int = 8765,
    /** 直连候选所属班级（服务端下发插件地址时带出）；为空表示未知，如旧服务端或手动扫描。 */
    val lanClassId: String = "",
    /** 多班级账号最近选择的班级，冷启动后恢复，避免每次都回到第一个班级。 */
    val selectedClassId: String = "",
    val themeId: String = "lavender",
    val updateChannel: UpdateChannel = UpdateChannel.STABLE,
    val forceUpdateEnabled: Boolean = false,
    /** 启动时自动检查 App 更新并弹窗提示；默认开启。 */
    val autoUpdateCheck: Boolean = true,
    /** 用户在更新弹窗中选择“跳过此版本”的版本号；更高的新版本仍会提示。 */
    val skippedUpdateVersion: String = "",
    /** 上次自动检查更新的时间（Unix 毫秒），用于限制检查频率。 */
    val lastUpdateCheckAt: Long = 0L,
    val receiveOnClass: Boolean = true,
    val receiveOnBreaking: Boolean = true,
    val receiveAfterSchool: Boolean = true,
    val receiveScheduleChanged: Boolean = true,
    val receiveCustom: Boolean = true,
    val receiveAutomationNotifications: Boolean = true,
    val receivePluginNotifications: Boolean = true,
    val appearanceMode: String = "system",
)

class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("remoteci", Context.MODE_PRIVATE)

    fun load(): WatchSettings = WatchSettings(
        username = prefs.getString(KEY_USERNAME, "") ?: "",
        cloudConnectionEnabled = prefs.getBoolean(KEY_CLOUD_CONNECTION_ENABLED, true),
        cloudServerUrl = prefs.getString(KEY_CLOUD_URL, "http://10.0.2.2:8080") ?: "http://10.0.2.2:8080",
        lanConnectionEnabled = prefs.getBoolean(KEY_LAN_CONNECTION_ENABLED, true),
        lanHost = prefs.getString(KEY_LAN_HOST, "") ?: "",
        lanHostCandidates = prefs.getString(KEY_LAN_HOST_CANDIDATES, "")
            ?.lineSequence()?.filter(String::isNotBlank)?.distinct()?.toList().orEmpty(),
        lanPort = prefs.getInt(KEY_LAN_PORT, 8765),
        lanClassId = prefs.getString(KEY_LAN_CLASS_ID, "") ?: "",
        selectedClassId = prefs.getString(KEY_SELECTED_CLASS_ID, "") ?: "",
        themeId = prefs.getString(KEY_THEME_ID, "lavender") ?: "lavender",
        updateChannel = runCatching {
            UpdateChannel.valueOf(prefs.getString(KEY_UPDATE_CHANNEL, UpdateChannel.STABLE.name)!!)
        }.getOrDefault(UpdateChannel.STABLE),
        forceUpdateEnabled = prefs.getBoolean(KEY_FORCE_UPDATE, false),
        autoUpdateCheck = prefs.getBoolean(KEY_AUTO_UPDATE_CHECK, true),
        skippedUpdateVersion = prefs.getString(KEY_SKIPPED_UPDATE, "") ?: "",
        lastUpdateCheckAt = prefs.getLong(KEY_LAST_UPDATE_CHECK, 0L),
        receiveOnClass = prefs.getBoolean(KEY_ON_CLASS, true),
        receiveOnBreaking = prefs.getBoolean(KEY_ON_BREAKING, true),
        receiveAfterSchool = prefs.getBoolean(KEY_AFTER_SCHOOL, true),
        receiveScheduleChanged = prefs.getBoolean(KEY_SCHEDULE, true),
        receiveCustom = prefs.getBoolean(KEY_CUSTOM, true),
        receiveAutomationNotifications = prefs.getBoolean(KEY_AUTOMATION_NOTIFICATIONS, true),
        receivePluginNotifications = prefs.getBoolean(KEY_PLUGIN_NOTIFICATIONS, true),
        appearanceMode = prefs.getString(KEY_APPEARANCE, "system") ?: "system",
    )

    fun save(settings: WatchSettings) {
        prefs.edit()
            .putString(KEY_USERNAME, settings.username)
            .putBoolean(KEY_CLOUD_CONNECTION_ENABLED, settings.cloudConnectionEnabled)
            .putString(KEY_CLOUD_URL, settings.cloudServerUrl)
            .putBoolean(KEY_LAN_CONNECTION_ENABLED, settings.lanConnectionEnabled)
            .putString(KEY_LAN_HOST, settings.lanHost)
            .putString(KEY_LAN_HOST_CANDIDATES, settings.lanHostCandidates.joinToString("\n"))
            .putInt(KEY_LAN_PORT, settings.lanPort)
            .putString(KEY_LAN_CLASS_ID, settings.lanClassId)
            .putString(KEY_SELECTED_CLASS_ID, settings.selectedClassId)
            .putString(KEY_THEME_ID, settings.themeId)
            .putString(KEY_UPDATE_CHANNEL, settings.updateChannel.name)
            .putBoolean(KEY_FORCE_UPDATE, settings.forceUpdateEnabled)
            .putBoolean(KEY_AUTO_UPDATE_CHECK, settings.autoUpdateCheck)
            .putString(KEY_SKIPPED_UPDATE, settings.skippedUpdateVersion)
            .putLong(KEY_LAST_UPDATE_CHECK, settings.lastUpdateCheckAt)
            .putBoolean(KEY_ON_CLASS, settings.receiveOnClass)
            .putBoolean(KEY_ON_BREAKING, settings.receiveOnBreaking)
            .putBoolean(KEY_AFTER_SCHOOL, settings.receiveAfterSchool)
            .putBoolean(KEY_SCHEDULE, settings.receiveScheduleChanged)
            .putBoolean(KEY_CUSTOM, settings.receiveCustom)
            .putBoolean(KEY_AUTOMATION_NOTIFICATIONS, settings.receiveAutomationNotifications)
            .putBoolean(KEY_PLUGIN_NOTIFICATIONS, settings.receivePluginNotifications)
            .putString(KEY_APPEARANCE, settings.appearanceMode)
            .apply()
    }

    private companion object {
        const val KEY_USERNAME = "username"
        const val KEY_CLOUD_CONNECTION_ENABLED = "cloudConnectionEnabled"
        const val KEY_CLOUD_URL = "cloudServerUrl"
        const val KEY_LAN_CONNECTION_ENABLED = "lanConnectionEnabled"
        const val KEY_LAN_HOST = "lanHost"
        const val KEY_LAN_HOST_CANDIDATES = "lanHostCandidates"
        const val KEY_LAN_PORT = "lanPort"
        const val KEY_LAN_CLASS_ID = "lanClassId"
        const val KEY_SELECTED_CLASS_ID = "selectedClassId"
        const val KEY_THEME_ID = "themeId"
        const val KEY_UPDATE_CHANNEL = "updateChannel"
        const val KEY_FORCE_UPDATE = "forceUpdateEnabled"
        const val KEY_AUTO_UPDATE_CHECK = "autoUpdateCheck"
        const val KEY_SKIPPED_UPDATE = "skippedUpdateVersion"
        const val KEY_LAST_UPDATE_CHECK = "lastUpdateCheckAt"
        const val KEY_ON_CLASS = "receiveOnClass"
        const val KEY_ON_BREAKING = "receiveOnBreaking"
        const val KEY_AFTER_SCHOOL = "receiveAfterSchool"
        const val KEY_SCHEDULE = "receiveScheduleChanged"
        const val KEY_CUSTOM = "receiveCustom"
        const val KEY_AUTOMATION_NOTIFICATIONS = "receiveAutomationNotifications"
        const val KEY_PLUGIN_NOTIFICATIONS = "receivePluginNotifications"
        const val KEY_APPEARANCE = "appearanceMode"
    }
}

/**
 * 合并服务端下发的候选地址；仍可用的当前首选地址保持优先，避免每次重连重复试错。
 * 同时记录候选所属班级，换班后不会再直连到上一个班级的插件。
 */
internal fun mergePluginNetworkInfo(settings: WatchSettings, info: PluginNetworkInfo): WatchSettings {
    if (info.port !in 1..65535) return settings
    val advertised = info.addresses.map(String::trim).filter(String::isNotEmpty).distinct()
    if (advertised.isEmpty()) return settings.copy(lanPort = info.port)
    val preferred = settings.lanHost.takeIf(advertised::contains) ?: advertised.first()
    return settings.copy(
        lanHost = preferred,
        lanHostCandidates = listOf(preferred) + advertised.filterNot { it == preferred },
        lanPort = info.port,
        lanClassId = info.classId ?: settings.lanClassId,
    )
}

internal fun lanEndpointHosts(settings: WatchSettings): List<String> =
    (listOf(settings.lanHost) + settings.lanHostCandidates)
        .map(String::trim)
        .filter(String::isNotEmpty)
        .distinct()

internal fun lanWebSocketUrl(host: String, port: Int): String {
    val urlHost = if (':' in host && !host.startsWith("[")) "[$host]" else host
    return "ws://$urlHost:$port/ws"
}

/** 用选中的插件补齐两条连接信息；localhost 必须替换成手表实际连到的插件地址。 */
internal fun mergeLanBootstrapInfo(
    settings: WatchSettings,
    candidate: LanPluginCandidate,
    bootstrap: ConnectionBootstrapInfo,
): WatchSettings {
    val cloudUrl = reachableCloudServerUrl(bootstrap.cloudServerUrl, candidate.host)
        ?: throw IllegalArgumentException("插件返回的云服务器地址无效")
    return settings.copy(
        cloudServerUrl = cloudUrl,
        lanHost = candidate.host,
        lanHostCandidates = listOf(candidate.host),
        lanPort = candidate.port,
        // 扫描选中的插件属于哪个班级未知，等服务端下发插件地址后再补齐。
        lanClassId = "",
    )
}

private fun reachableCloudServerUrl(value: String, pluginHost: String): String? = runCatching {
    val parsed = URI(value.trim())
    val parsedHost = parsed.host?.takeIf(String::isNotBlank) ?: return null
    if (parsed.scheme !in listOf("http", "https")) return null
    val host = if (parsedHost.lowercase() in setOf("localhost", "127.0.0.1", "0.0.0.0", "::1", "::"))
        pluginHost
    else
        parsedHost
    URI(parsed.scheme, parsed.userInfo, host, parsed.port, parsed.path, parsed.query, parsed.fragment)
        .toString().trimEnd('/')
}.getOrNull()

internal fun WatchSettings.receives(event: ClassEvent): Boolean = when (event.event) {
    Protocol.EVENT_ON_CLASS -> receiveOnClass
    Protocol.EVENT_ON_BREAKING -> receiveOnBreaking
    Protocol.EVENT_AFTER_SCHOOL -> receiveAfterSchool
    Protocol.EVENT_SCHEDULE_CHANGED -> receiveScheduleChanged
    Protocol.EVENT_CUSTOM -> receiveCustom
    Protocol.EVENT_AUTOMATION_NOTIFICATION -> receiveAutomationNotifications
    Protocol.EVENT_PLUGIN_NOTIFICATION -> receivePluginNotifications
    else -> false
}
