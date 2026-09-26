using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>配对请求体。</summary>
public sealed class PairRequest
{
    [JsonPropertyName("pairCode")]
    public required string PairCode { get; set; }

    /// <summary>申请角色：plugin 或 watch。</summary>
    [JsonPropertyName("role")]
    public required string Role { get; set; }
}

/// <summary>配对成功响应。</summary>
public sealed class PairResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; set; }

    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>统一错误响应。</summary>
public sealed class ApiError
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}

/// <summary>插件长期凭证的管理视图（不含令牌本身）。</summary>
public sealed class PluginCredentialInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>插件归属班级名称；旧版服务端为 null。</summary>
    [JsonPropertyName("className")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>REST 错误码常量。</summary>
public static class ApiErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string PairCodeInvalid = "PAIR_CODE_INVALID";
    public const string InternalError = "INTERNAL_ERROR";
    /// <summary>用户名已存在（409）。</summary>
    public const string UsernameExists = "USERNAME_EXISTS";
    /// <summary>不能移除最后一个管理员（409）。</summary>
    public const string LastAdmin = "LAST_ADMIN";
    /// <summary>WS 协议版本不受支持，连接被拒绝。</summary>
    public const string ProtocolVersionUnsupported = "PROTOCOL_VERSION_UNSUPPORTED";
    /// <summary>参与端未共同声明该功能能力。</summary>
    public const string CapabilityUnsupported = "CAPABILITY_UNSUPPORTED";
}
