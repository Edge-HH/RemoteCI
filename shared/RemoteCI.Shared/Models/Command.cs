using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

public sealed class CommandMessage
{
    [JsonPropertyName("command")]
    public CommandKind Command { get; set; }

    [JsonPropertyName("scheduleChange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScheduleChangeRequest? ScheduleChange { get; set; }

    [JsonPropertyName("notification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NotificationRequest? Notification { get; set; }

    [JsonPropertyName("voiceMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VoiceMessageRequest? VoiceMessage { get; set; }

    [JsonPropertyName("mainMenuVisible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MainMenuVisible { get; set; }

    [JsonPropertyName("powerAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PowerActionKind? PowerAction { get; set; }

    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VolumeControlRequest? Volume { get; set; }

    /// <summary>RunExtension 命令的目标扩展 Id（由插件注册表提供）。</summary>
    [JsonPropertyName("extensionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtensionId { get; set; }

    /// <summary>RunExtension 命令的参数键值对；值统一以字符串传输。</summary>
    [JsonPropertyName("extensionArgs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? ExtensionArgs { get; set; }

    /// <summary>接入端覆盖此字段，插件只信任经服务端或本地挑战认证后的身份。</summary>
    [JsonPropertyName("requestedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserProfile? RequestedBy { get; set; }
}

public sealed class VolumeControlRequest
{
    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Level { get; set; }

    [JsonPropertyName("muted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Muted { get; set; }
}

public sealed class CommandResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("scheduleRevision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScheduleRevision { get; set; }

    /// <summary>失败回执统一工厂，服务端与插件端共用，避免各处重复同一辅助实现。</summary>
    public static CommandResult Failure(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}

public static class CommandResultCodes
{
    public const string Ok = "OK";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Forbidden = "FORBIDDEN";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string PluginOffline = "PLUGIN_OFFLINE";
    public const string CapabilityUnsupported = "CAPABILITY_UNSUPPORTED";
    public const string Timeout = "COMMAND_TIMEOUT";
    public const string ScheduleStale = "SCHEDULE_STALE";
    public const string ScheduleUnavailable = "SCHEDULE_UNAVAILABLE";
    public const string SaveFailed = "SAVE_FAILED";
    public const string InternalError = "INTERNAL_ERROR";
    /// <summary>单连接命令频率超限（滑动窗口限速）。</summary>
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
    /// <summary>目标扩展上一次执行尚未结束，拒绝重复触发。</summary>
    public const string Busy = "BUSY";
}
