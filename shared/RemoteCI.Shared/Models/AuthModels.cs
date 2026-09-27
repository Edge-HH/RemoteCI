using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

public sealed class LoginRequest
{
    /// <summary>唯一登录 ID；为保持协议兼容，JSON 字段名仍为 username。</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "Wear OS";
}

public sealed class RefreshSessionRequest
{
    [JsonPropertyName("deviceSessionId")]
    public Guid DeviceSessionId { get; set; }

    [JsonPropertyName("deviceSecret")]
    public string DeviceSecret { get; set; } = string.Empty;
}

public sealed class AuthResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("accessExpiresAt")]
    public DateTimeOffset AccessExpiresAt { get; set; }

    [JsonPropertyName("deviceSessionId")]
    public Guid DeviceSessionId { get; set; }

    /// <summary>仅在登录或续期响应中返回，服务端只持久化其 SHA-256 摘要。</summary>
    [JsonPropertyName("deviceSecret")]
    public string DeviceSecret { get; set; } = string.Empty;

    [JsonPropertyName("deviceExpiresAt")]
    public DateTimeOffset DeviceExpiresAt { get; set; }

    [JsonPropertyName("user")]
    public UserProfile User { get; set; } = new();

    /// <summary>
    /// 批量导入且未设置密码的账号首次登录时为 true：AccessToken 为空，
    /// 客户端应改走 /api/auth/setup-password 设置密码后重新登录。
    /// </summary>
    [JsonPropertyName("passwordPending")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PasswordPending { get; set; }

    /// <summary>一次性密码设置令牌，15 分钟有效；仅 passwordPending 为 true 时返回。</summary>
    [JsonPropertyName("setupToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetupToken { get; set; }
}

/// <summary>首登设置密码请求：凭登录时下发的一次性令牌补设密码。</summary>
public sealed class SetupPasswordRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("setupToken")]
    public string SetupToken { get; set; } = string.Empty;

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class UserProfile : UserProfileLike
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>唯一登录 ID。</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>用户可见的用户名，可重复，不参与登录。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; }

    [JsonPropertyName("roleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("roleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }

    [JsonPropertyName("grantedPermissions")]
    public UserPermissions GrantedPermissions { get; set; }

    [JsonPropertyName("permissions")]
    public UserPermissions Permissions { get; set; }

    /// <summary>
    /// 服务端允许该账号调用的扩展 Id。null 表示旧版服务端未下发策略，
    /// 空列表表示当前没有可调用扩展。
    /// </summary>
    [JsonPropertyName("allowedExtensionIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedExtensionIds { get; set; }

    /// <summary>该账号选择在自己手表上展示的扩展 Id。</summary>
    [JsonPropertyName("visibleExtensionIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VisibleExtensionIds { get; set; }

    /// <summary>该账号可访问的班级列表；null 表示旧版服务端未下发班级信息。</summary>
    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClassSummary>? Classes { get; set; }

    [JsonPropertyName("version")]
    public long Version { get; set; }

    /// <summary>
    /// 以指定权限位生成身份副本：命令的 RequestedBy 必须携带目标班级的有效权限，
    /// 插件端只信任消息内的权限位，局域网直连与云端路径保持一致。
    /// </summary>
    public UserProfile WithPermissions(UserPermissions permissions) => new()
    {
        Id = Id,
        Username = Username,
        DisplayName = DisplayName,
        Role = Role,
        RoleId = RoleId,
        RoleName = RoleName,
        GrantedPermissions = GrantedPermissions,
        Permissions = permissions,
        AllowedExtensionIds = AllowedExtensionIds,
        VisibleExtensionIds = VisibleExtensionIds,
        Classes = Classes,
        Version = Version,
    };
}

public sealed class AuthChallenge
{
    [JsonPropertyName("challengeId")]
    public string ChallengeId { get; set; } = string.Empty;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>局域网认证证明；不携带密码、设备密钥或云端访问令牌。</summary>
public sealed class AuthProof
{
    [JsonPropertyName("challengeId")]
    public string ChallengeId { get; set; } = string.Empty;

    [JsonPropertyName("deviceSessionId")]
    public Guid DeviceSessionId { get; set; }

    [JsonPropertyName("clientNonce")]
    public string ClientNonce { get; set; } = string.Empty;

    [JsonPropertyName("proof")]
    public string Proof { get; set; } = string.Empty;
}

public sealed class AuthState
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }

    /// <summary>当前认证连接所属的 WebUI/服务端版本，用于限制手表更新上限。</summary>
    [JsonPropertyName("serverVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerVersion { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserProfile? User { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>服务端通过已认证插件连接下发的版本化授权镜像。</summary>
public sealed class AccountSync
{
    [JsonPropertyName("version")]
    public long Version { get; set; }

    /// <summary>
    /// 生成该镜像的服务端实例标识。服务端数据库重建后版本号可能回退，
    /// 插件据此识别实例变化并强制覆盖镜像；旧版服务端不携带该字段（空 Guid）。
    /// </summary>
    [JsonPropertyName("serverInstanceId")]
    public Guid ServerInstanceId { get; set; }

    /// <summary>生成授权镜像的 WebUI/服务端版本，供局域网连接转发给手表。</summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>服务端显式声明的能力；null 表示旧 V3 服务端，按基础能力处理。</summary>
    [JsonPropertyName("serverCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ServerCapabilities { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>镜像目标班级名称；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("className")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    [JsonPropertyName("accounts")]
    public List<SyncedAccount> Accounts { get; set; } = [];

    [JsonPropertyName("sessions")]
    public List<SyncedDeviceSession> Sessions { get; set; } = [];
}

public sealed class SyncedAccount
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; }

    [JsonPropertyName("roleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("roleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }

    [JsonPropertyName("grantedPermissions")]
    public UserPermissions GrantedPermissions { get; set; }

    [JsonPropertyName("effectivePermissions")]
    public UserPermissions EffectivePermissions { get; set; }

    [JsonPropertyName("allowedExtensionIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedExtensionIds { get; set; }

    [JsonPropertyName("visibleExtensionIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VisibleExtensionIds { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("version")]
    public long Version { get; set; }

    public UserProfile ToProfile() => new()
    {
        Id = Id,
        Username = Username,
        DisplayName = DisplayName,
        Role = Role,
        GrantedPermissions = GrantedPermissions,
        Permissions = EffectivePermissions,
        AllowedExtensionIds = AllowedExtensionIds,
        VisibleExtensionIds = VisibleExtensionIds,
        Version = Version,
    };
}

public sealed class SyncedDeviceSession
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    /// <summary>设备密钥的 SHA-256 摘要，作为局域网 HMAC 密钥使用。</summary>
    [JsonPropertyName("verifier")]
    public string Verifier { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class DeviceSessionSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("current")]
    public bool Current { get; set; }
}

public sealed class CreateUserRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; } = UserRole.User;

    [JsonPropertyName("roleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("grantedPermissions")]
    public UserPermissions GrantedPermissions { get; set; }
}

public sealed class UpdateUserRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; }

    [JsonPropertyName("roleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("roleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }

    [JsonPropertyName("grantedPermissions")]
    public UserPermissions GrantedPermissions { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class ChangePasswordRequest
{
    [JsonPropertyName("currentPassword")]
    public string CurrentPassword { get; set; } = string.Empty;

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>账号自助修改用户可见用户名（DisplayName）；登录 ID（Username）不可自行修改。</summary>
public sealed class ChangeDisplayNameRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public sealed class UserListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; }

    [JsonPropertyName("roleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("roleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }

    [JsonPropertyName("grantedPermissions")]
    public UserPermissions GrantedPermissions { get; set; }

    [JsonPropertyName("effectivePermissions")]
    public UserPermissions EffectivePermissions { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AccountRoleInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("defaultPermissions")]
    public UserPermissions DefaultPermissions { get; set; }
    [JsonPropertyName("userCount")]
    public int UserCount { get; set; }
}

public sealed class CreateAccountRoleRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("defaultPermissions")]
    public UserPermissions DefaultPermissions { get; set; }
}

public sealed class UpdateAccountRoleRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("defaultPermissions")]
    public UserPermissions DefaultPermissions { get; set; }
}


/// <summary>
/// API Key 管理视图；密钥明文只在创建时返回一次，服务端仅保存摘要。
/// </summary>
public sealed class ApiKeyInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>用于在列表中区分密钥的前缀，不包含完整密钥。</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("revokedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>创建 API Key 时的可选名称。</summary>
public sealed class CreateApiKeyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>API Key 创建结果；Key 仅在本次响应中出现。</summary>
public sealed class ApiKeyCreationResult
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public ApiKeyInfo ApiKey { get; set; } = new();

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
}
