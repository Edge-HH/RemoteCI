package com.remoteci.mobile.data

import java.time.Instant
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement

/** 协议消息必须写出默认字段，尤其是每个 WebSocket 信封都必需的 protocolVersion。 */
internal val protocolJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
}

internal fun encodeEnvelope(envelope: Envelope): String =
    protocolJson.encodeToString(Envelope.serializer(), envelope)

object Protocol {
    const val VERSION = 3
    const val TYPE_STATE_PUSH = "state_push"
    const val TYPE_SCHEDULE_SYNC = "schedule_sync"
    const val TYPE_SCHEDULE_PULL = "schedule_pull"
    const val TYPE_SCHEDULE_SYNC_STATUS = "schedule_sync_status"
    const val TYPE_EVENT_NOTIFY = "event_notify"
    const val TYPE_EXTENSIONS_SYNC = "extensions_sync"
    const val TYPE_COMMAND = "command"
    const val TYPE_COMMAND_RESULT = "command_result"
    const val TYPE_AUTH_CHALLENGE = "auth_challenge"
    const val TYPE_AUTH_PROOF = "auth_proof"
    const val TYPE_AUTH_STATE = "auth_state"
    const val TYPE_SETTINGS_SYNC = "settings_sync"
    const val TYPE_PLUGIN_NETWORK_INFO = "plugin_network_info"
    const val TYPE_CONNECTION_BOOTSTRAP = "connection_bootstrap"
    const val TYPE_PEER_CAPABILITIES = "peer_capabilities"
    const val TYPE_CAPABILITIES_SYNC = "capabilities_sync"
    /** 服务端发给当前用户所有在线连接的个人通知（换课申请等），不按班级过滤。 */
    const val TYPE_USER_NOTIFY = "user_notify"
    const val LAN_DISCOVERY_PORT = 48765
    const val LAN_DISCOVERY_REQUEST = "REMOTECI_DISCOVER_V3"

    const val STATE_NONE = 0
    const val STATE_CLASS = 1
    const val STATE_BREAKING = 2
    const val STATE_AFTER_SCHOOL = 3
    const val STATE_PREPARE_CLASS = 4

    const val EVENT_ON_CLASS = 1
    const val EVENT_ON_BREAKING = 2
    const val EVENT_AFTER_SCHOOL = 3
    const val EVENT_SCHEDULE_CHANGED = 4
    const val EVENT_CUSTOM = 5
    const val EVENT_AUTOMATION_NOTIFICATION = 6
    const val EVENT_PLUGIN_NOTIFICATION = 7

    const val CMD_CHANGE_SCHEDULE = 1
    const val CMD_SEND_NOTIFICATION = 2
    const val CMD_CLEAR_NOTIFICATIONS = 3
    const val CMD_SET_MAIN_MENU_VISIBILITY = 4
    const val CMD_POWER = 5
    const val CMD_VOLUME = 6
    const val CMD_RUN_EXTENSION = 7
    const val CMD_TEACHER_COMING = 8
    const val CMD_SEND_VOICE_MESSAGE = 9
    const val POWER_SHUTDOWN = 1
    const val POWER_RESTART = 2
    const val POWER_SLEEP = 3
    const val POWER_HIBERNATE = 4
    const val CHANGE_EXCHANGE = 1
    const val CHANGE_REPLACE = 2

    const val EXT_PARAM_TEXT = 1
    const val EXT_PARAM_NUMBER = 2
    const val EXT_PARAM_SWITCH = 3
    const val EXT_PARAM_SELECT = 4

    const val ROLE_USER = 1
    const val ROLE_ADMIN = 2
    const val ROLE_KIND_CLASS_ADMINISTRATOR = 4

    /** 服务端全局角色种类（AccountRoleKind）：内置“老师”角色按显示名与课表教师名绑定任教班级。 */
    const val ROLE_KIND_TEACHER = 5

    /** 课表/“我的日程”条目来源：插件从未同步过课表，按服务端班级档案推算。 */
    const val BUNDLE_SOURCE_PROFILE = "profile"
    const val PERMISSION_VIEW_CURRENT = 1
    const val PERMISSION_ACCESS_WEB_UI = 2
    const val PERMISSION_MANAGE_USERS = 4
    const val PERMISSION_SEND_NOTIFICATIONS = 8
    const val PERMISSION_MANAGE_SCHEDULE = 16
    const val PERMISSION_POWER_CONTROL = 32
    const val PERMISSION_SYSTEM_CONTROL = PERMISSION_POWER_CONTROL
    const val PERMISSION_TEACHER_COMING = 64
    const val PERMISSION_RUN_EXTENSIONS = 128
    const val PERMISSION_MAIN_MENU_CONTROL = 256
    const val PERMISSION_SEND_VOICE_MESSAGES = 512
    const val PERMISSION_CHANGE_DISPLAY_NAME = 1024
    const val PERMISSION_API_ACCESS = 2048
    /** 老师主动发起换课申请（由对方老师审批）。 */
    const val PERMISSION_REQUEST_SWAP = 4096
    /** 不经审批的强制换课；对方老师可撤回。 */
    const val PERMISSION_FORCE_SWAP = 8192
    /** 经服务端中转把文件发到有权班级的设备。 */
    const val PERMISSION_SEND_FILES = 16384
    /** 获取有权班级教室电脑的当前屏幕截图；班主任角色默认拥有。 */
    const val PERMISSION_CAPTURE_SCREEN = 32768

    /** 以下为权限细化后拆出的独立权限（第 16 位起）；旧服务端不会下发，判断时见 [hasPermission]。 */
    const val PERMISSION_MANAGE_CLASS_PROFILE = 1 shl 16
    const val PERMISSION_PULL_SCHEDULE = 1 shl 17
    const val PERMISSION_CLEAR_NOTIFICATIONS = 1 shl 18
    const val PERMISSION_VOLUME_CONTROL = 1 shl 19
    const val PERMISSION_MAIN_WINDOW_LAYOUT = 1 shl 20
    const val PERMISSION_REMOTE_TERMINAL = 1 shl 21
    const val PERMISSION_CONTROL_CLASSISLAND = 1 shl 22
    const val PERMISSION_CONFIGURE_EXTENSIONS = 1 shl 23
    const val PERMISSION_MANAGE_PLUGINS = 1 shl 24
    const val PERMISSION_UPGRADE_SOFTWARE = 1 shl 25
    const val PERMISSION_MANAGE_DEVICE_GUARD = 1 shl 26
    const val PERMISSION_MANAGE_CLASS_INFO = 1 shl 27
    const val PERMISSION_MANAGE_CLASS_BOARDS = 1 shl 28
    const val PERMISSION_VIEW_AUDIT_LOG = 1 shl 29
    const val PERMISSION_VIEW_LESSON_STATS = 1 shl 30
    /** 第 16–30 位：只有权限细化后的服务端才会下发。 */
    const val REFINED_PERMISSIONS = 0x7FFF0000
    /** 管理员拥有的全部班级权限位。 */
    const val PERMISSION_ALL = Int.MAX_VALUE
    /** 可授予普通账号与角色的班级权限：除基础权限与兼容保留的“修改用户名”外的全部。 */
    const val PERMISSION_ASSIGNABLE = PERMISSION_ALL and PERMISSION_VIEW_CURRENT.inv() and PERMISSION_CHANGE_DISPLAY_NAME.inv()

    /** 系统管理权限（managementPermissions），只按全局角色计算。 */
    const val MANAGEMENT_MANAGE_ROLES = 1
    const val MANAGEMENT_IMPORT_USERS = 2
    const val MANAGEMENT_MANAGE_CLASSES = 4
    const val MANAGEMENT_MANAGE_PROFILE_LIBRARY = 8
    const val MANAGEMENT_MANAGE_HOLIDAYS = 16
    const val MANAGEMENT_VIEW_DEVICES = 32
    const val MANAGEMENT_BATCH_CONTROL = 64
    const val MANAGEMENT_MANAGE_AUTOMATION = 128
    const val MANAGEMENT_MANAGE_APP_GUARD = 256
    const val MANAGEMENT_MANAGE_EXTENSION_SETTINGS = 512
    const val MANAGEMENT_MANAGE_LOGIN_PAGE = 1024
    const val MANAGEMENT_MANAGE_PLUGIN_ACCESS = 2048
    const val MANAGEMENT_VIEW_ALL_AUDIT_LOGS = 4096
    const val MANAGEMENT_VIEW_ALL_LESSON_STATS = 8192
    const val MANAGEMENT_ALL = 16383

    /** 权限细化前合并在一起的旧权限位；旧服务端不认识新位时按它判断。 */
    fun legacyPermission(permission: Int): Int = when (permission) {
        PERMISSION_CLEAR_NOTIFICATIONS -> PERMISSION_SEND_NOTIFICATIONS
        PERMISSION_VOLUME_CONTROL -> PERMISSION_POWER_CONTROL
        PERMISSION_MAIN_WINDOW_LAYOUT -> PERMISSION_MAIN_MENU_CONTROL
        else -> permission
    }

    /** 权限是否满足：服务端已细化权限时按新位判断，旧服务端按原来合并在一起的旧位判断。 */
    fun hasPermission(effective: Int, permission: Int, refined: Boolean): Boolean {
        val required = if (refined) permission else legacyPermission(permission)
        return effective and required == required
    }

    fun hasRefinedBits(permissions: Int): Boolean = permissions and REFINED_PERMISSIONS != 0

    const val SCHEDULE_SOURCE_PLUGIN = 1
    const val SCHEDULE_SOURCE_WEB_UI = 2
    const val SCHEDULE_SOURCE_WATCH = 3
    const val SCHEDULE_SOURCE_AUTOMATIC = 4
    const val SCHEDULE_SOURCE_CONNECTION = 5
    const val SCHEDULE_SOURCE_MOBILE = 6

    const val SCHEDULE_TASK_RUNNING = 1
    const val SCHEDULE_TASK_COMPLETED = 2
    const val SCHEDULE_TASK_FAILED = 3
    const val SCHEDULE_TASK_BUSY = 4

    const val CAP_CLASS_STATE_READ = "class-state.read"
    const val CAP_SCHEDULE_READ = "schedule.read"
    const val CAP_SCHEDULE_PULL = "schedule.pull"
    const val CAP_SCHEDULE_CHANGE = "schedule.change"
    const val CAP_NOTIFICATION_SEND = "notification.send"
    const val CAP_VOICE_MESSAGE_SEND = "voice-message.send"
    const val CAP_NOTIFICATION_CLEAR = "notification.clear"
    const val CAP_TEACHER_COMING = "teacher-coming"
    const val CAP_MAIN_MENU_VISIBILITY = "main-menu.visibility"
    const val CAP_POWER_CONTROL = "power.control"
    const val CAP_VOLUME_CONTROL = "volume.control"
    const val CAP_EXTENSIONS_RUN = "extensions.run"
    /** 屏幕截图：经服务端 REST 发起，插件截屏后上传到服务端。 */
    const val CAP_SCREEN_CAPTURE = "screen.capture"

    val BASELINE_CAPABILITIES = setOf(
        CAP_CLASS_STATE_READ,
        CAP_SCHEDULE_READ,
        CAP_SCHEDULE_PULL,
        CAP_SCHEDULE_CHANGE,
        CAP_NOTIFICATION_SEND,
        CAP_NOTIFICATION_CLEAR,
        CAP_TEACHER_COMING,
        CAP_MAIN_MENU_VISIBILITY,
        CAP_POWER_CONTROL,
        CAP_VOLUME_CONTROL,
        CAP_EXTENSIONS_RUN,
    )
    val CURRENT_CAPABILITIES = BASELINE_CAPABILITIES + CAP_VOICE_MESSAGE_SEND + CAP_SCREEN_CAPTURE
}

@Serializable
data class Envelope(
    @SerialName("protocolVersion") val protocolVersion: Int = Protocol.VERSION,
    val type: String,
    @SerialName("messageId") val messageId: String = "",
    @SerialName("replyToMessageId") val replyToMessageId: String? = null,
    // 服务端与插件按 DateTimeOffset 解析：空字符串会让整条消息被丢弃，必须写出有效的 ISO-8601 时间。
    val timestamp: String = Instant.now().toString(),
    val sender: Int? = null,
    val payload: JsonElement? = null,
)

@Serializable
data class PeerCapabilities(
    @SerialName("softwareVersion") val softwareVersion: String = "",
    val capabilities: List<String> = emptyList(),
)

@Serializable
data class CapabilitiesSync(
    val server: PeerCapabilities = PeerCapabilities(capabilities = Protocol.BASELINE_CAPABILITIES.toList()),
    /** 旧版服务端或局域网直连时唯一的插件能力；新版服务端改用 [classPlugins] 按班级下发。 */
    val plugin: PeerCapabilities? = null,
    /** 每个可访问班级当前主插件的能力；未出现的班级表示该班插件离线。旧版服务端为 null。 */
    @SerialName("classPlugins") val classPlugins: List<ClassPluginCapabilities>? = null,
)

@Serializable
data class ClassPluginCapabilities(
    @SerialName("classId") val classId: String = "",
    val plugin: PeerCapabilities = PeerCapabilities(),
)

@Serializable
data class ClassStateSnapshot(
    @SerialName("classId") val classId: String? = null,
    @SerialName("scheduleDate") val scheduleDate: String? = null,
    @SerialName("currentSubject") val currentSubject: String? = null,
    @SerialName("nextClassSubject") val nextClassSubject: String? = null,
    @SerialName("currentState") val currentState: Int = Protocol.STATE_NONE,
    @SerialName("currentTimeLayoutItem") val currentTimeLayoutItem: String? = null,
    @SerialName("timeZoneOffsetMinutes") val timeZoneOffsetMinutes: Int? = null,
    @SerialName("nextClassTimeLayoutItem") val nextClassTimeLayoutItem: String? = null,
    @SerialName("classPlanName") val classPlanName: String? = null,
    @SerialName("isClassPlanEnabled") val isClassPlanEnabled: Boolean = false,
    @SerialName("isClassPlanLoaded") val isClassPlanLoaded: Boolean = false,
    @SerialName("onClassLeftTime") val onClassLeftTime: String? = null,
    @SerialName("onBreakingLeftTime") val onBreakingLeftTime: String? = null,
    @SerialName("lessonConfirmed") val lessonConfirmed: Boolean = false,
    @SerialName("isNotificationPlaying") val isNotificationPlaying: Boolean = false,
    @SerialName("isMainMenuVisible") val isMainMenuVisible: Boolean = true,
    @SerialName("isSleepAvailable") val isSleepAvailable: Boolean = false,
    @SerialName("isHibernateAvailable") val isHibernateAvailable: Boolean = false,
    @SerialName("isVolumeControlAvailable") val isVolumeControlAvailable: Boolean = false,
    @SerialName("volumePercent") val volumePercent: Int = 0,
    @SerialName("isMuted") val isMuted: Boolean = false,
    @SerialName("generatedAt") val generatedAt: String? = null,
)

@Serializable
data class ScheduleSyncRequest(
    @SerialName("taskId") val taskId: String,
    val source: Int = Protocol.SCHEDULE_SOURCE_WATCH,
    @SerialName("classId") val classId: String? = null,
)

@Serializable
data class ScheduleSyncStatus(
    @SerialName("taskId") val taskId: String = "",
    val source: Int = 0,
    val state: Int = Protocol.SCHEDULE_TASK_RUNNING,
    val message: String = "",
    @SerialName("startedAt") val startedAt: String? = null,
    @SerialName("finishedAt") val finishedAt: String? = null,
    @SerialName("activeTaskId") val activeTaskId: String? = null,
    @SerialName("classId") val classId: String? = null,
)

@Serializable
data class ScheduleBundle(
    @SerialName("classId") val classId: String? = null,
    @SerialName("fromDate") val fromDate: String = "",
    @SerialName("generatedAt") val generatedAt: String? = null,
    val days: List<ScheduleDay> = emptyList(),
    val subjects: List<SubjectEntry> = emptyList(),
    /** "profile"：插件从未同步过课表，服务端按班级档案推算，只供查看，不能据此换课；插件课表不带该字段。 */
    val source: String? = null,
) {
    val fromProfile: Boolean get() = source == Protocol.BUNDLE_SOURCE_PROFILE
}

@Serializable
data class ScheduleDay(
    val date: String,
    val revision: String,
    @SerialName("classPlanName") val classPlanName: String? = null,
    val enabled: Boolean = false,
    val courses: List<CourseEntry> = emptyList(),
)

@Serializable
data class CourseEntry(
    val index: Int,
    val label: String,
    @SerialName("subjectId") val subjectId: String,
    val subject: String,
    @SerialName("startTime") val startTime: String? = null,
    @SerialName("endTime") val endTime: String? = null,
    /** 授课教师名，来自教室端 ClassIsland 档案；旧版服务端不下发。 */
    val teacher: String? = null,
    val enabled: Boolean = true,
    val personalCourseId: String? = null,
    val startsAt: String? = null,
    val endsAt: String? = null,
    val reminderMinutes: Int? = null,
    val notifyAtStart: Boolean = false,
)

@Serializable
data class SubjectEntry(val id: String, val name: String, val teacher: String? = null)

/** “我的日程”：当前老师账号按显示名绑定课表后跨班级聚合的个人课表。 */
@Serializable
data class MyScheduleResponse(
    @SerialName("fromDate") val fromDate: String = "",
    @SerialName("generatedAt") val generatedAt: String? = null,
    val days: List<MyScheduleDay> = emptyList(),
)

@Serializable
data class MyScheduleDay(val date: String, val items: List<MyScheduleItem> = emptyList())

@Serializable
data class MyScheduleItem(
    @SerialName("classId") val classId: String,
    @SerialName("className") val className: String,
    val courses: List<CourseEntry> = emptyList(),
    /** "profile"：该班插件从未同步过课表，课程按服务端班级档案推算，可能与教室端实际课表不同。 */
    val source: String? = null,
    /** 课程钟点所在班级的 UTC 偏移；旧服务端缺省时使用本机时区。 */
    val timeZoneOffsetMinutes: Int? = null,
)

/** “我的日程”当前/下一节课，时间已经由服务端按班级时区换算为绝对时间。 */
@Serializable
data class MyNextCourseResponse(
    val at: String = "",
    val current: MyCourseSlot? = null,
    val next: MyCourseSlot? = null,
)

@Serializable
data class MyCourseSlot(
    val date: String = "",
    @SerialName("classId") val classId: String = "",
    @SerialName("className") val className: String = "",
    val course: CourseEntry = CourseEntry(index = 0, label = "", subjectId = "", subject = ""),
    @SerialName("startsAt") val startsAt: String = "",
    @SerialName("endsAt") val endsAt: String = "",
)

@Serializable
data class ClassEvent(
    val id: String = "",
    @SerialName("classId") val classId: String? = null,
    val event: Int,
    val subject: String? = null,
    val message: String? = null,
    @SerialName("occurredAt") val occurredAt: String? = null,
)

@Serializable
data class ScheduleChangeRequest(
    val date: String,
    val mode: Int,
    @SerialName("sourceIndex") val sourceIndex: Int,
    @SerialName("targetIndex") val targetIndex: Int? = null,
    @SerialName("replacementSubjectId") val replacementSubjectId: String? = null,
    @SerialName("expectedRevision") val expectedRevision: String,
    val permanent: Boolean = false,
)

@Serializable
data class NotificationRequest(
    val title: String,
    val message: String,
    @SerialName("forceSenderInTitle") val forceSenderInTitle: Boolean? = null,
    @SerialName("isNotificationEffectEnabled") val isNotificationEffectEnabled: Boolean = false,
    @SerialName("isNotificationSoundEnabled") val isNotificationSoundEnabled: Boolean = false,
    @SerialName("isSpeechEnabled") val isSpeechEnabled: Boolean = false,
    @SerialName("isNotificationTopmostEnabled") val isNotificationTopmostEnabled: Boolean = false,
    // 与服务端 NotificationRequest 一致：null 由插件按 5 秒 / 1 次处理。
    @SerialName("durationSeconds") val durationSeconds: Int? = null,
    @SerialName("repeatCounts") val repeatCounts: Int? = null,
    // 三态：null（旧客户端省略）由插件保持升级前的滚动正文；本客户端总是显式发送 true/false。
    @SerialName("isRollingEnabled") val isRollingEnabled: Boolean? = null,
) {
    companion object {
        const val MAX_TITLE_LENGTH = 60
        const val MAX_MESSAGE_LENGTH = 500
        const val DEFAULT_DURATION_SECONDS = 5
        const val MAX_DURATION_SECONDS = 3600
        const val MAX_REPEAT_COUNTS = 10
        const val ROLLING_SUGGESTION_THRESHOLD = 30

        /** 正文超过阈值且未开启滚动时，静态正文可能显示不全，发送界面应提示开启滚动。 */
        fun shouldSuggestRolling(message: String, isRollingEnabled: Boolean): Boolean =
            !isRollingEnabled && message.trim().let { it.codePointCount(0, it.length) } > ROLLING_SUGGESTION_THRESHOLD
    }
}

@Serializable
data class SettingsSync(
    @SerialName("forceSenderInTitle") val forceSenderInTitle: Boolean = true,
    @SerialName("updatedAt") val updatedAt: String? = null,
)

@Serializable
data class PluginNetworkInfo(
    @SerialName("lanServerEnabled") val lanServerEnabled: Boolean = false,
    val addresses: List<String> = emptyList(),
    val port: Int = 8765,
    /** 插件所属班级；旧服务端不下发，表示单班级部署。 */
    @SerialName("classId") val classId: String? = null,
)

@Serializable
data class LanDiscoveryResponse(
    @SerialName("protocolVersion") val protocolVersion: Int = Protocol.VERSION,
    @SerialName("instanceName") val instanceName: String,
    val port: Int,
)

data class LanPluginCandidate(
    val instanceName: String,
    val host: String,
    val port: Int,
)

@Serializable
data class ConnectionBootstrapInfo(
    @SerialName("instanceName") val instanceName: String,
    @SerialName("cloudServerUrl") val cloudServerUrl: String,
)

@Serializable
data class CommandMessage(
    val command: Int,
    @SerialName("classId") val classId: String? = null,
    @SerialName("scheduleChange") val scheduleChange: ScheduleChangeRequest? = null,
    val notification: NotificationRequest? = null,
    @SerialName("voiceMessage") val voiceMessage: VoiceMessageRequest? = null,
    @SerialName("mainMenuVisible") val mainMenuVisible: Boolean? = null,
    @SerialName("powerAction") val powerAction: Int? = null,
    val volume: VolumeControlRequest? = null,
    @SerialName("extensionId") val extensionId: String? = null,
    @SerialName("extensionArgs") val extensionArgs: Map<String, String?>? = null,
)

@Serializable
data class VoiceMessageRequest(
    val format: String = "pcm_s16le_16000_mono",
    @SerialName("audioBase64") val audioBase64: String,
    /** 课表端播放结束前不允许暂停或关闭语音浮窗。 */
    @SerialName("forceComplete") val forceComplete: Boolean = false,
)

@Serializable
data class VolumeControlRequest(
    val level: Int? = null,
    val muted: Boolean? = null,
)

@Serializable
data class CommandResult(
    val success: Boolean = false,
    val code: String = "",
    val message: String = "",
    @SerialName("scheduleRevision") val scheduleRevision: String? = null,
)

@Serializable
data class ExtensionDefinition(
    val id: String,
    @SerialName("classId") val classId: String? = null,
    @SerialName("displayName") val displayName: String,
    val icon: String? = null,
    @SerialName("requiredPermission") val requiredPermission: Int = Protocol.PERMISSION_VIEW_CURRENT,
    val parameters: List<ExtensionParameter> = emptyList(),
)

@Serializable
data class ExtensionParameter(
    val key: String,
    val label: String,
    val type: Int = Protocol.EXT_PARAM_TEXT,
    @SerialName("defaultValue") val defaultValue: String? = null,
    val required: Boolean = false,
    val options: List<String> = emptyList(),
    /** 与 options 按下标对应的显示名称；缺失或数量不一致时显示原值。 */
    @SerialName("optionLabels") val optionLabels: List<String>? = null,
    val description: String? = null,
    val placeholder: String? = null,
    val multiline: Boolean = false,
    val min: Double? = null,
    val max: Double? = null,
)

@Serializable
data class ClassSummary(
    val id: String = "",
    val name: String = "",
    @SerialName("roleName") val roleName: String? = null,
    @SerialName("roleKind") val roleKind: Int? = null,
    val permissions: Int? = null,
    @SerialName("visitorEnabled") val visitorEnabled: Boolean = false,
    @SerialName("groupName") val groupName: String? = null,
) {
    /** 本班班主任：优先按角色种类判断；旧版服务端或插件镜像未下发 roleKind 时退回内置角色名。 */
    val isClassAdministrator: Boolean
        get() = roleKind?.let { it == Protocol.ROLE_KIND_CLASS_ADMINISTRATOR } ?: (roleName == "班主任" || roleName == "班管理员")
    val effectivePermissions: Int
        get() = permissions ?: Protocol.PERMISSION_VIEW_CURRENT
}

@Serializable
data class UserProfile(
    val id: String = "",
    val username: String = "",
    @SerialName("displayName") val displayName: String = "",
    val role: Int = Protocol.ROLE_USER,
    @SerialName("roleId") val roleId: String? = null,
    @SerialName("roleName") val roleName: String? = null,
    @SerialName("roleKind") val roleKind: Int? = null,
    @SerialName("grantedPermissions") val grantedPermissions: Int = 0,
    val permissions: Int = Protocol.PERMISSION_VIEW_CURRENT,
    /** 系统管理权限；只有权限细化后的服务端会下发（可能为 0），旧服务端与局域网镜像为 null。 */
    @SerialName("managementPermissions") val managementPermissions: Int? = null,
    @SerialName("allowedExtensionIds") val allowedExtensionIds: List<String>? = null,
    @SerialName("visibleExtensionIds") val visibleExtensionIds: List<String>? = null,
    val classes: List<ClassSummary>? = null,
    val version: Long = 0,
) {
    val isAdmin: Boolean get() = role == Protocol.ROLE_ADMIN

    /** 内置“老师”角色（按显示名绑定课表教师名），roleKind 免受角色改名影响。 */
    val isTeacher: Boolean get() = roleKind == Protocol.ROLE_KIND_TEACHER
    /** 拥有个人“我的日程”的内置角色：老师与班主任（均按显示名匹配课表教师名）。 */
    val hasPersonalSchedule: Boolean
        get() = roleKind == Protocol.ROLE_KIND_TEACHER || roleKind == Protocol.ROLE_KIND_CLASS_ADMINISTRATOR
    /**
     * 服务端是否已细化权限：会下发 managementPermissions，或权限中带第 16 位起的拆分权限。
     * 旧服务端两者都没有，此时按原来合并在一起的旧权限判断。
     */
    val usesRefinedPermissions: Boolean
        get() = managementPermissions != null || Protocol.hasRefinedBits(permissions) ||
            classes?.any { Protocol.hasRefinedBits(it.effectivePermissions) } == true

    /** 按细化后的权限判断全局权限；旧服务端不下发新权限位时退回旧权限。 */
    fun can(permission: Int): Boolean = Protocol.hasPermission(permissions, permission, usesRefinedPermissions)

    /**
     * 是否可拉取指定班级的课表（与服务端按班级校验一致）：系统管理员；该班有“从教室刷新课表”权限；
     * 旧服务端没有这一权限位时，退回原来的“本班班主任”判断。
     */
    fun canPullScheduleFor(classId: String?): Boolean {
        if (isAdmin) return true
        val classContext = classes?.firstOrNull { it.id == classId } ?: return false
        return if (usesRefinedPermissions)
            Protocol.hasPermission(classContext.effectivePermissions, Protocol.PERMISSION_PULL_SCHEDULE, refined = true)
        else classContext.isClassAdministrator
    }
    val roleLabel: String
        get() = when {
            isAdmin -> "管理员"
            isTeacher -> roleName ?: "老师"
            else -> roleName ?: "用户"
        }
    fun has(permission: Int): Boolean = permissions and permission == permission

    /** 是否拥有某项系统管理权限；系统管理员始终拥有。旧服务端不下发时为 false，调用方按旧规则判断。 */
    fun hasManagement(permission: Int): Boolean =
        isAdmin || (managementPermissions ?: 0) and permission == permission

    fun canInvoke(extension: ExtensionDefinition): Boolean =
        has(Protocol.PERMISSION_RUN_EXTENSIONS) &&
            (allowedExtensionIds == null || extension.id in allowedExtensionIds)

    fun showsOnWatch(extension: ExtensionDefinition): Boolean =
        canInvoke(extension) &&
            (visibleExtensionIds == null || extension.id in visibleExtensionIds)
}

@Serializable
data class LoginRequest(val username: String, val password: String, @SerialName("deviceName") val deviceName: String,
    val twoFactorCode: String? = null, val useRecoveryCode: Boolean = false)

@Serializable
data class MobileLoginRequest(
    val ticket: String,
    @SerialName("deviceName") val deviceName: String,
    val twoFactorCode: String? = null,
    val useRecoveryCode: Boolean = false,
)

/** WebUI 登录页“手机扫码”二维码：服务器地址与挑战码；已登录的 App 扫码确认后由显示二维码的浏览器登录。 */
data class WebLoginQrPayload(val serverUrl: String, val code: String)

/** 解析 `remoteci://weblogin?server=…&code=…`；不是网页登录二维码时返回 null。 */
internal fun parseWebLoginQrPayload(raw: String): WebLoginQrPayload? {
    val uri = runCatching { java.net.URI(raw.trim()) }.getOrNull() ?: return null
    if (!uri.scheme.equals("remoteci", ignoreCase = true) || !uri.host.equals("weblogin", ignoreCase = true)) return null
    val params = parseQueryParameters(uri.rawQuery)
    val serverUrl = params["server"]?.takeIf(String::isNotBlank) ?: return null
    val code = params["code"]?.takeIf(String::isNotBlank) ?: return null
    return WebLoginQrPayload(serverUrl, code)
}

/** WebUI 扫码登录二维码的解析结果：服务器地址、登录 ID 与一次性票据。 */
data class LoginQrPayload(val serverUrl: String, val username: String, val ticket: String)

/**
 * 解析 WebUI 生成的 `remoteci://login?...` 扫码登录二维码。
 * 旧版只含服务器地址的普通 http(s) 地址返回 null，由调用方回退为地址输入。
 */
internal fun parseLoginQrPayload(raw: String): LoginQrPayload? {
    val uri = runCatching { java.net.URI(raw.trim()) }.getOrNull() ?: return null
    if (!uri.scheme.equals("remoteci", ignoreCase = true) || !uri.host.equals("login", ignoreCase = true)) return null
    val params = parseQueryParameters(uri.rawQuery)
    val serverUrl = params["server"]?.takeIf(String::isNotBlank) ?: return null
    val ticket = params["ticket"]?.takeIf(String::isNotBlank) ?: return null
    return LoginQrPayload(serverUrl, params["user"].orEmpty(), ticket)
}

private fun parseQueryParameters(rawQuery: String?): Map<String, String> {
    if (rawQuery.isNullOrBlank()) return emptyMap()
    return rawQuery.split('&').mapNotNull { part ->
        val separator = part.indexOf('=')
        if (separator < 0) return@mapNotNull null
        val key = java.net.URLDecoder.decode(part.substring(0, separator), Charsets.UTF_8.name())
        val value = java.net.URLDecoder.decode(part.substring(separator + 1), Charsets.UTF_8.name())
        key to value
    }.toMap()
}

@Serializable
data class SetupPasswordRequest(
    val username: String,
    @SerialName("setupToken") val setupToken: String,
    @SerialName("newPassword") val newPassword: String,
)

/** 首登设置密码挑战：界面据此展示设置表单。 */
data class PasswordSetupChallenge(val username: String, val setupToken: String)

@Serializable
data class RefreshSessionRequest(
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("deviceSecret") val deviceSecret: String,
)

@Serializable
data class AuthResponse(
    val twoFactorRequired: Boolean? = null,
    @SerialName("accessToken") val accessToken: String,
    @SerialName("passwordPending") val passwordPending: Boolean? = null,
    @SerialName("setupToken") val setupToken: String? = null,
    @SerialName("accessExpiresAt") val accessExpiresAt: String,
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("deviceSecret") val deviceSecret: String,
    @SerialName("deviceExpiresAt") val deviceExpiresAt: String,
    val user: UserProfile = UserProfile(),
)

@Serializable
data class AuthChallenge(
    @SerialName("challengeId") val challengeId: String,
    val nonce: String,
    @SerialName("expiresAt") val expiresAt: String,
)

@Serializable
data class AuthProof(
    @SerialName("challengeId") val challengeId: String,
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("clientNonce") val clientNonce: String,
    val proof: String,
)

@Serializable
data class AuthState(
    val authenticated: Boolean,
    @SerialName("serverVersion") val serverVersion: String? = null,
    val user: UserProfile? = null,
    @SerialName("errorCode") val errorCode: String? = null,
    val error: String? = null,
)

@Serializable
data class PersistedDeviceSession(
    val username: String,
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("deviceSecret") val deviceSecret: String,
    @SerialName("deviceExpiresAt") val deviceExpiresAt: String,
)
