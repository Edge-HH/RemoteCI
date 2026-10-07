package com.remoteci.mobile.data

import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

class AdminApiException(message: String) : IOException(message)

object AdminApi {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private val http = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()
    private val media = "application/json; charset=utf-8".toMediaType()

    suspend fun users(): List<UserListItem> = get("/api/users", ListSerializer(UserListItem.serializer()))
    suspend fun createUser(body: CreateUserRequest): UserListItem = post("/api/users", body, CreateUserRequest.serializer(), UserListItem.serializer())
    suspend fun updateUser(id: String, body: UpdateUserRequest): UserListItem = put("/api/users/$id", body, UpdateUserRequest.serializer(), UserListItem.serializer())
    suspend fun deleteUser(id: String) { request("DELETE", "/api/users/$id") }
    suspend fun resetPassword(id: String, password: String) {
        post("/api/users/$id/password", ResetPasswordRequest(password), ResetPasswordRequest.serializer(), UnitSerializer)
    }

    suspend fun roles(): List<AccountRoleInfo> = get("/api/roles", ListSerializer(AccountRoleInfo.serializer()))
    suspend fun createRole(body: AccountRoleMutation): AccountRoleInfo =
        post("/api/roles", body, AccountRoleMutation.serializer(), AccountRoleInfo.serializer())
    suspend fun updateRole(id: String, body: AccountRoleMutation) {
        put("/api/roles/$id", body, AccountRoleMutation.serializer(), UnitSerializer)
    }
    suspend fun deleteRole(id: String) { request("DELETE", "/api/roles/$id") }

    suspend fun visitor(): VisitorAccessState = get("/api/visitor", VisitorAccessState.serializer())
    suspend fun setVisitor(body: VisitorAccessState): VisitorAccessState =
        put("/api/visitor", body, VisitorAccessState.serializer(), VisitorAccessState.serializer())

    suspend fun sessions(): List<DeviceSessionSummary> = get("/api/me/sessions", ListSerializer(DeviceSessionSummary.serializer()))
    suspend fun revokeSession(id: String) { request("DELETE", "/api/me/sessions/$id") }
    suspend fun changePassword(current: String, next: String) {
        post("/api/me/password", ChangePasswordRequest(current, next), ChangePasswordRequest.serializer(), UnitSerializer)
    }

    /** “我的日程”：老师账号按显示名绑定课表后的跨班级个人课表。 */
    suspend fun mySchedule(): MyScheduleResponse = get("/api/me/schedule", MyScheduleResponse.serializer())
    /** 老师状态栏/超级岛使用的当前课与下一节课。 */
    suspend fun myNextCourse(): MyNextCourseResponse = get("/api/me/schedule/next", MyNextCourseResponse.serializer())
    suspend fun updateDisplayName(name: String) {
        post("/api/me/display-name", UpdateDisplayNameRequest(name), UpdateDisplayNameRequest.serializer(), UnitSerializer)
    }

    /** 换课申请：选择器数据（所有班级七日课表与本人任教学科）。 */
    suspend fun swapCatalog(): SwapCatalog = get("/api/swap-requests/catalog", SwapCatalog.serializer())
    /** box：incoming 待我处理 / outgoing 我发起的。 */
    suspend fun swapRequests(box: String): List<SwapRequestView> =
        get("/api/swap-requests?box=${enc(box)}", ListSerializer(SwapRequestView.serializer()))
    suspend fun createSwap(body: CreateSwapRequest): SwapRequestView =
        post("/api/swap-requests", body, CreateSwapRequest.serializer(), SwapRequestView.serializer())
    suspend fun approveSwap(id: String, note: String?): SwapRequestView =
        post("/api/swap-requests/${enc(id)}/approve", SwapDecisionRequest(note), SwapDecisionRequest.serializer(), SwapRequestView.serializer())
    suspend fun rejectSwap(id: String, note: String?): SwapRequestView =
        post("/api/swap-requests/${enc(id)}/reject", SwapDecisionRequest(note), SwapDecisionRequest.serializer(), SwapRequestView.serializer())
    suspend fun cancelSwap(id: String): SwapRequestView =
        post("/api/swap-requests/${enc(id)}/cancel", EmptyBody(), EmptyBody.serializer(), SwapRequestView.serializer())
    suspend fun revokeSwap(id: String): SwapRequestView =
        post("/api/swap-requests/${enc(id)}/revoke", EmptyBody(), EmptyBody.serializer(), SwapRequestView.serializer())

    /** 个人通知；after 为上次收到的最新通知时间，用于重连后补齐离线期间的通知。 */
    suspend fun notifications(after: String? = null, unreadOnly: Boolean = false): List<UserNotification> {
        val query = buildList {
            if (after != null) add("after=${enc(after)}")
            if (unreadOnly) add("unread=true")
        }.joinToString("&")
        return get("/api/me/notifications" + if (query.isEmpty()) "" else "?$query", ListSerializer(UserNotification.serializer()))
    }
    suspend fun markNotificationsRead(ids: List<String>? = null, all: Boolean = false) {
        post("/api/me/notifications/read", MarkNotificationsReadRequest(ids, all), MarkNotificationsReadRequest.serializer(), UnitSerializer)
    }

    /** 当前登录账号的最新档案（/api/me），修改显示名后用于刷新本地班级与权限。 */
    suspend fun me(): UserProfile = get("/api/me", UserProfile.serializer())

    suspend fun pairingCode(): PairingCodeResponse = post("/api/plugin/pairing-code", EmptyBody(), EmptyBody.serializer(), PairingCodeResponse.serializer())
    suspend fun pluginCredentials(): List<PluginCredentialInfo> =
        get("/api/plugins/credentials", ListSerializer(PluginCredentialInfo.serializer()))
    suspend fun revokePluginCredential(id: String) { request("DELETE", "/api/plugins/credentials/$id") }

    /**
     * 一键打开 WebUI：为当前账号换取 1 分钟一次性网页登录票据，返回可直接交给浏览器的完整地址
     * （服务端兑换后写入 WebUI 登录 Cookie 并跳到 returnUrl）。
     */
    suspend fun webLoginUrl(returnUrl: String? = null, classId: String? = null): String {
        val base = ConnectionManager.restBaseUrl() ?: throw AdminApiException("尚未连接服务器")
        val ticket = post("/api/auth/web-ticket", EmptyBody(), EmptyBody.serializer(), WebLoginTicket.serializer())
        val query = buildString {
            if (returnUrl != null) append("&returnUrl=").append(enc(returnUrl))
            if (classId != null) append("&classId=").append(enc(classId))
        }
        return base + ticket.path + query
    }

    suspend fun adminStatus(): AdminStatus = get("/api/admin/status", AdminStatus.serializer())
    suspend fun notificationSettings(): SettingsSync = get("/api/settings/notifications", SettingsSync.serializer())
    suspend fun setNotificationSettings(forceSenderInTitle: Boolean): SettingsSync =
        put("/api/settings/notifications", SettingsSync(forceSenderInTitle), SettingsSync.serializer(), SettingsSync.serializer())

    suspend fun schedulePull(): SchedulePullSetting = get("/api/settings/schedule-pull", SchedulePullSetting.serializer())
    suspend fun setSchedulePull(intervalMinutes: Int): SchedulePullSetting =
        put("/api/settings/schedule-pull", SchedulePullSetting(intervalMinutes), SchedulePullSetting.serializer(), SchedulePullSetting.serializer())

    suspend fun extensions(classId: String? = null): List<ExtensionPolicyItem> =
        get(
            if (classId == null) "/api/extensions"
            else "/api/extensions?classId=${java.net.URLEncoder.encode(classId, Charsets.UTF_8.name())}",
            ListSerializer(ExtensionPolicyItem.serializer()),
        )
    suspend fun updateExtensionPolicy(id: String, body: ExtensionPolicyUpdate) {
        put("/api/extensions/${java.net.URLEncoder.encode(id, Charsets.UTF_8.name())}", body, ExtensionPolicyUpdate.serializer(), UnitSerializer)
    }

    suspend fun backups(): List<BackupFileInfo> = get("/api/admin/backups", ListSerializer(BackupFileInfo.serializer()))
    suspend fun createBackup() { post("/api/admin/backups", EmptyBody(), EmptyBody.serializer(), UnitSerializer) }
    suspend fun deleteBackup(name: String) { request("DELETE", "/api/admin/backups/${enc(name)}") }
    suspend fun restoreBackup(name: String) { post("/api/admin/backups/${enc(name)}/restore", EmptyBody(), EmptyBody.serializer(), UnitSerializer) }

    suspend fun systemInfo(): SystemInfo = get("/api/admin/system", SystemInfo.serializer())
    suspend fun checkUpdate(channel: String): ReleaseInfo? = post("/api/admin/updates/check", UpdateCheckRequest(channel), UpdateCheckRequest.serializer(), ReleaseInfo.serializer())
    suspend fun applyUpdate(channel: String, force: Boolean) {
        post("/api/admin/updates/apply", UpdateApplyRequest(channel, force), UpdateApplyRequest.serializer(), UnitSerializer)
    }

    private fun enc(value: String) = java.net.URLEncoder.encode(value, Charsets.UTF_8.name())

    private suspend fun <T> get(path: String, serializer: kotlinx.serialization.DeserializationStrategy<T>): T =
        decode(request("GET", path), serializer)

    private suspend fun <T, R> post(
        path: String,
        body: T,
        input: kotlinx.serialization.SerializationStrategy<T>,
        output: kotlinx.serialization.DeserializationStrategy<R>,
    ): R = decode(request("POST", path, json.encodeToString(input, body)), output)

    private suspend fun <T, R> put(
        path: String,
        body: T,
        input: kotlinx.serialization.SerializationStrategy<T>,
        output: kotlinx.serialization.DeserializationStrategy<R>,
    ): R = decode(request("PUT", path, json.encodeToString(input, body)), output)

    private fun <T> decode(text: String, serializer: kotlinx.serialization.DeserializationStrategy<T>): T {
        if (serializer === UnitSerializer) return Unit as T
        if (text.isBlank()) {
            @Suppress("UNCHECKED_CAST")
            if (serializer === UnitSerializer) return Unit as T
        }
        return json.decodeFromString(serializer, text)
    }

    private suspend fun request(method: String, path: String, body: String? = null): String = withContext(Dispatchers.IO) {
        val base = ConnectionManager.restBaseUrl() ?: throw AdminApiException("尚未连接服务器")
        val token = ConnectionManager.restToken() ?: throw AdminApiException("尚未登录")
        val builder = Request.Builder()
            .url("$base$path")
            .header("Authorization", "Bearer $token")
        when (method) {
            "GET" -> builder.get()
            "DELETE" -> builder.delete()
            "POST" -> builder.post((body ?: "{}").toRequestBody(media))
            "PUT" -> builder.put((body ?: "{}").toRequestBody(media))
            else -> error(method)
        }
        http.newCall(builder.build()).execute().use { response ->
            val text = response.body.string()
            if (!response.isSuccessful) {
                val message = runCatching { json.decodeFromString(ApiError.serializer(), text).message }.getOrNull()
                throw AdminApiException(message ?: "HTTP ${response.code}")
            }
            text
        }
    }
}

private object UnitSerializer : kotlinx.serialization.DeserializationStrategy<Unit> {
    override val descriptor = kotlinx.serialization.descriptors.buildClassSerialDescriptor("Unit")
    override fun deserialize(decoder: kotlinx.serialization.encoding.Decoder) = Unit
}

@Serializable data class EmptyBody(val ok: Boolean = true)

/** /api/auth/web-ticket 的响应：path 是相对服务器根的一次性登录地址。 */
@Serializable data class WebLoginTicket(val ticket: String = "", val path: String = "")

@Serializable data class UpdateDisplayNameRequest(val displayName: String)

@Serializable
data class UserListItem(
    val id: String = "",
    val username: String = "",
    @SerialName("displayName") val displayName: String = "",
    val role: Int = Protocol.ROLE_USER,
    @SerialName("roleId") val roleId: String? = null,
    @SerialName("roleName") val roleName: String? = null,
    @SerialName("grantedPermissions") val grantedPermissions: Int = 0,
    @SerialName("effectivePermissions") val effectivePermissions: Int = 0,
    val enabled: Boolean = true,
    @SerialName("updatedAt") val updatedAt: String? = null,
)

@Serializable
data class CreateUserRequest(
    val username: String,
    @SerialName("displayName") val displayName: String,
    val password: String,
    val role: Int = Protocol.ROLE_USER,
    @SerialName("roleId") val roleId: String? = null,
    @SerialName("grantedPermissions") val grantedPermissions: Int = 0,
)

@Serializable
data class UpdateUserRequest(
    @SerialName("displayName") val displayName: String,
    val role: Int,
    @SerialName("roleId") val roleId: String? = null,
    @SerialName("grantedPermissions") val grantedPermissions: Int = 0,
    val enabled: Boolean,
)

@Serializable data class ResetPasswordRequest(val password: String)
@Serializable
data class ChangePasswordRequest(
    @SerialName("currentPassword") val currentPassword: String,
    @SerialName("newPassword") val newPassword: String,
)

@Serializable
data class AccountRoleInfo(
    val id: String = "",
    val name: String = "",
    val kind: String = "",
    @SerialName("defaultPermissions") val defaultPermissions: Int = 0,
    @SerialName("userCount") val userCount: Int = 0,
)

@Serializable
data class AccountRoleMutation(
    val name: String,
    @SerialName("defaultPermissions") val defaultPermissions: Int,
)

@Serializable
data class VisitorAccessState(val enabled: Boolean = false, @SerialName("autoEnter") val autoEnter: Boolean = false)

@Serializable
data class DeviceSessionSummary(
    val id: String = "",
    @SerialName("deviceName") val deviceName: String = "",
    @SerialName("createdAt") val createdAt: String? = null,
    @SerialName("lastSeenAt") val lastSeenAt: String? = null,
    @SerialName("expiresAt") val expiresAt: String? = null,
    val current: Boolean = false,
)

@Serializable data class PairingCodeResponse(@SerialName("pairCode") val pairCode: String = "")

@Serializable
data class PluginCredentialInfo(
    val id: String = "",
    val name: String = "",
    val enabled: Boolean = true,
    @SerialName("createdAt") val createdAt: String? = null,
    @SerialName("lastSeenAt") val lastSeenAt: String? = null,
)

@Serializable
data class AdminStatus(
    val pluginOnline: Boolean = false,
    val pluginConnections: Int = 0,
    val watchConnections: Int = 0,
    val mobileConnections: Int = 0,
    val accountCount: Int = 0,
    val latestStateAt: String? = null,
    val latestScheduleAt: String? = null,
    val protocolVersion: Int = Protocol.VERSION,
)

@Serializable
data class ExtensionPolicyItem(
    val id: String = "",
    @SerialName("displayName") val displayName: String = "",
    val enabled: Boolean = true,
    @SerialName("allowNonAdmin") val allowNonAdmin: Boolean = false,
    @SerialName("showOnWatch") val showOnWatch: Boolean = true,
    @SerialName("canInvoke") val canInvoke: Boolean = false,
)

@Serializable
data class ExtensionPolicyUpdate(
    val enabled: Boolean? = null,
    @SerialName("allowNonAdmin") val allowNonAdmin: Boolean? = null,
    @SerialName("showOnWatch") val showOnWatch: Boolean? = null,
)

@Serializable data class SchedulePullSetting(@SerialName("intervalMinutes") val intervalMinutes: Int = 0)
@Serializable
data class BackupFileInfo(
    val name: String = "",
    @SerialName("createdAt") val createdAt: String? = null,
    val size: Long = 0,
    val source: String = "",
)
@Serializable data class SystemInfo(val currentVersion: String = "", val canSelfUpdate: Boolean = false, val message: String = "")
@Serializable data class UpdateCheckRequest(val channel: String)
@Serializable data class UpdateApplyRequest(val channel: String, val force: Boolean = false)
@Serializable data class ReleaseInfo(val tag: String = "", val name: String? = null)
@Serializable data class ApiError(val code: String = "", val message: String = "")
