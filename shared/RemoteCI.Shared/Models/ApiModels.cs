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

    /// <summary>使用统一连接码时，插件端填写的班级名备注；班级配对码忽略该字段。</summary>
    [JsonPropertyName("classNameRemark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassNameRemark { get; set; }
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

    /// <summary>统一连接码设备在未分配列表中提供的班级名备注。</summary>
    [JsonPropertyName("classNameRemark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassNameRemark { get; set; }

    [JsonPropertyName("assigned")]
    public bool Assigned { get; set; }

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
    /// <summary>换课申请涉及的课位已被修改（409），需要重新申请。</summary>
    public const string SwapSlotChanged = "SWAP_SLOT_CHANGED";
    /// <summary>该老师当天对这节课的强制换课已被撤回，不能再次强制（409）。</summary>
    public const string SwapForceLocked = "SWAP_FORCE_LOCKED";
    /// <summary>目标班级没有同名学科，无法跨班换入（400）。</summary>
    public const string SwapSubjectMissing = "SWAP_SUBJECT_MISSING";
    /// <summary>换课的两节课中至少要有一节是申请人自己的课（400）。</summary>
    public const string SwapNotOwn = "SWAP_NOT_OWN";
    /// <summary>申请当前状态不允许该操作（409），例如已被处理。</summary>
    public const string SwapStateConflict = "SWAP_STATE_CONFLICT";
    /// <summary>档案已被其他操作修改，提交的修订号已过期（409），需要重新读取后再保存或下发。</summary>
    public const string ProfileStale = "PROFILE_STALE";
}
