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

    /// <summary>ApplyExtensionSettings 命令的目标分组与待修改设置。</summary>
    [JsonPropertyName("extensionSettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtensionSettingsRequest? ExtensionSettings { get; set; }

    /// <summary>插件或 ClassIsland 远程升级参数；仅升级命令使用。</summary>
    [JsonPropertyName("softwareUpgrade")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SoftwareUpgradeRequest? SoftwareUpgrade { get; set; }

    /// <summary>插件安装、卸载、启用或禁用参数。</summary>
    [JsonPropertyName("pluginManagement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginManagementRequest? PluginManagement { get; set; }

    /// <summary>RemoteCI 远程插件管理策略。</summary>
    [JsonPropertyName("pluginManagementPolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginManagementPolicyRequest? PluginManagementPolicy { get; set; }

    /// <summary>新增或整体替换 ClassIsland 时间表。</summary>
    [JsonPropertyName("timeLayoutUpdate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeLayoutUpdateRequest? TimeLayoutUpdate { get; set; }

    /// <summary>ClassIsland 档案分发参数。</summary>
    [JsonPropertyName("profileDistribution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileDistributionRequest? ProfileDistribution { get; set; }

    /// <summary>加入 ClassIsland 内置集控的参数。</summary>
    [JsonPropertyName("managementJoin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManagementJoinRequest? ManagementJoin { get; set; }

    /// <summary>设置科目授课教师的参数；仅 SetSubjectTeacher 命令使用。</summary>
    [JsonPropertyName("subjectTeacher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubjectTeacherRequest? SubjectTeacher { get; set; }

    /// <summary>远程终端命令执行参数；仅 ExecuteTerminalCommand 命令使用。</summary>
    [JsonPropertyName("terminalCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalCommandRequest? TerminalCommand { get; set; }

    /// <summary>文件分发参数；仅 SendFile 命令使用。</summary>
    [JsonPropertyName("fileDistribution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileDistributionRequest? FileDistribution { get; set; }

    /// <summary>命令目标班级；缺省时由服务端路由到默认班级或用户唯一成员班级。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

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

    /// <summary>可选附加数据：终端命令的标准输出/错误，或文件在设备上的保存路径。</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

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
    /// <summary>插件离线，请求已由服务端保存，插件重新上线后自动补发（目前用于扩展设置）。</summary>
    public const string Queued = "QUEUED";
}
