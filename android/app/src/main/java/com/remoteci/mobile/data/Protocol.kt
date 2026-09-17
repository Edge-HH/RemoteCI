package com.remoteci.mobile.data

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
    val CURRENT_CAPABILITIES = BASELINE_CAPABILITIES + CAP_VOICE_MESSAGE_SEND
}

@Serializable
data class Envelope(
    @SerialName("protocolVersion") val protocolVersion: Int = Protocol.VERSION,
    val type: String,
    @SerialName("messageId") val messageId: String = "",
    @SerialName("replyToMessageId") val replyToMessageId: String? = null,
    val timestamp: String = "",
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
    val plugin: PeerCapabilities? = null,
)

@Serializable
data class ClassStateSnapshot(
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
)

@Serializable
data class ScheduleBundle(
    @SerialName("fromDate") val fromDate: String = "",
    @SerialName("generatedAt") val generatedAt: String? = null,
    val days: List<ScheduleDay> = emptyList(),
    val subjects: List<SubjectEntry> = emptyList(),
)

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
    val enabled: Boolean = true,
)

@Serializable
data class SubjectEntry(val id: String, val name: String)

@Serializable
data class ClassEvent(
    val id: String = "",
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
)

@Serializable
data class NotificationRequest(
    val title: String,
    val message: String,
    @SerialName("forceSenderInTitle") val forceSenderInTitle: Boolean? = null,
    @SerialName("isNotificationEffectEnabled") val isNotificationEffectEnabled: Boolean = false,
    @SerialName("isNotificationSoundEnabled") val isNotificationSoundEnabled: Boolean = false,
    @SerialName("isSpeechEnabled") val isSpeechEnabled: Boolean = false,
)

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
)

@Serializable
data class UserProfile(
    val id: String = "",
    val username: String = "",
    @SerialName("displayName") val displayName: String = "",
    val role: Int = Protocol.ROLE_USER,
    @SerialName("grantedPermissions") val grantedPermissions: Int = 0,
    val permissions: Int = Protocol.PERMISSION_VIEW_CURRENT,
    @SerialName("allowedExtensionIds") val allowedExtensionIds: List<String>? = null,
    @SerialName("visibleExtensionIds") val visibleExtensionIds: List<String>? = null,
    val version: Long = 0,
) {
    val isAdmin: Boolean get() = role == Protocol.ROLE_ADMIN
    fun has(permission: Int): Boolean = permissions and permission == permission
    fun canInvoke(extension: ExtensionDefinition): Boolean =
        has(Protocol.PERMISSION_RUN_EXTENSIONS) &&
            (allowedExtensionIds == null || extension.id in allowedExtensionIds)

    fun showsOnWatch(extension: ExtensionDefinition): Boolean =
        canInvoke(extension) &&
            (visibleExtensionIds == null || extension.id in visibleExtensionIds)
}

@Serializable
data class LoginRequest(val username: String, val password: String, @SerialName("deviceName") val deviceName: String)

@Serializable
data class RefreshSessionRequest(
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("deviceSecret") val deviceSecret: String,
)

@Serializable
data class AuthResponse(
    @SerialName("accessToken") val accessToken: String,
    @SerialName("accessExpiresAt") val accessExpiresAt: String,
    @SerialName("deviceSessionId") val deviceSessionId: String,
    @SerialName("deviceSecret") val deviceSecret: String,
    @SerialName("deviceExpiresAt") val deviceExpiresAt: String,
    val user: UserProfile,
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
