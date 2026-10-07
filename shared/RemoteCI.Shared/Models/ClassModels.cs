using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>登录用户可见的班级摘要；供客户端班级选择/切换使用。</summary>
public sealed class ClassSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>该用户在班级中的角色名；系统管理员为“管理员”。</summary>
    [JsonPropertyName("roleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }

    /// <summary>
    /// 该用户在班级中的角色种类（AccountRoleKind）；null 表示旧版服务端未下发。
    /// 客户端据此识别班主任等内置角色，不受角色改名影响。
    /// </summary>
    [JsonPropertyName("roleKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RoleKind { get; set; }

    /// <summary>该用户在本班级内的有效权限（按成员角色计算）。</summary>
    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserPermissions? Permissions { get; set; }

    [JsonPropertyName("visitorEnabled")]
    public bool VisitorEnabled { get; set; }

    /// <summary>所属分组名列表；未分组为 null。</summary>
    [JsonPropertyName("groupNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? GroupNames { get; set; }

    /// <summary>是否已设置班头像；客户端据此决定是否加载 /api/classes/{id}/avatar。</summary>
    [JsonPropertyName("hasAvatar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasAvatar { get; set; }
}

/// <summary>管理员视角的班级详情。</summary>
public sealed class ClassDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("visitorEnabled")]
    public bool VisitorEnabled { get; set; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    [JsonPropertyName("pluginCount")]
    public int PluginCount { get; set; }

    /// <summary>所属分组 Id 列表；一个班级可属于多个分组。</summary>
    [JsonPropertyName("groupIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? GroupIds { get; set; }

    [JsonPropertyName("groupNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? GroupNames { get; set; }

    /// <summary>是否已设置班头像。</summary>
    [JsonPropertyName("hasAvatar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasAvatar { get; set; }

    /// <summary>是否已设置班级固定配对码（不返回明文，仅用于管理页提示）。</summary>
    [JsonPropertyName("hasPairingCode")]
    public bool HasPairingCode { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 班级分组（如年级、校区）：支持父子层级，批量操作与广播通知按组展开时包含全部子分组中的班级。
/// </summary>
public sealed class ClassGroupInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>父分组；null 表示根分组。</summary>
    [JsonPropertyName("parentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("parentName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentName { get; set; }

    /// <summary>层级深度：根为 1。</summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    /// <summary>直接归属的班级数量（不含子分组内的班级）。</summary>
    [JsonPropertyName("classCount")]
    public int ClassCount { get; set; }
}

public sealed class CreateClassGroupRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>父分组；null 创建根分组。</summary>
    [JsonPropertyName("parentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentId { get; set; }
}

public sealed class UpdateClassGroupRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>移动分组到新的父级；null 移到根。不能移动到自身或后代。</summary>
    [JsonPropertyName("parentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentId { get; set; }
}

/// <summary>整体替换一个分组包含的班级（仅直接归属；子分组不受影响）。</summary>
public sealed class UpdateGroupClassesRequest
{
    [JsonPropertyName("classIds")]
    public List<Guid> ClassIds { get; set; } = [];
}

/// <summary>整体替换一个班级所属的分组。</summary>
public sealed class UpdateClassGroupsRequest
{
    [JsonPropertyName("groupIds")]
    public List<Guid> GroupIds { get; set; } = [];
}

/// <summary>批量导入人员请求：见 UserImportService 的行格式说明。</summary>
public sealed class BatchImportRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>3 列旧格式行使用的默认班级。</summary>
    [JsonPropertyName("defaultClassId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultClassId { get; set; }

    /// <summary>3 列旧格式行使用的默认角色；缺省为学生。</summary>
    [JsonPropertyName("defaultRoleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultRoleId { get; set; }
}

public sealed class BatchImportResult
{
    [JsonPropertyName("created")]
    public int Created { get; set; }

    [JsonPropertyName("failures")]
    public List<string> Failures { get; set; } = [];
}

/// <summary>把班级划入分组；groupId 为 null 表示移出分组。</summary>
public sealed class AssignClassGroupRequest
{
    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? GroupId { get; set; }
}

public sealed class CreateClassRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateClassRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ClassVisitorRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class ClassMemberInput
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; set; }
}

/// <summary>整体替换班级成员列表；不在列表中的现有成员会被移除。</summary>
public sealed class UpdateClassMembersRequest
{
    [JsonPropertyName("members")]
    public List<ClassMemberInput> Members { get; set; } = [];
}

public sealed class ClassMemberInfo
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; set; }

    [JsonPropertyName("roleName")]
    public string RoleName { get; set; } = string.Empty;
}

public sealed class BatchClassOperationRequest
{
    [JsonPropertyName("classIds")]
    public List<Guid> ClassIds { get; set; } = [];

    /// <summary>按分组批量：服务端会展开为组内全部班级后与 classIds 合并去重。</summary>
    [JsonPropertyName("groupIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? GroupIds { get; set; }

    /// <summary>enableVisitor / disableVisitor / delete。</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;
}

public sealed class BatchClassOperationResult
{
    [JsonPropertyName("results")]
    public List<BatchClassItemResult> Results { get; set; } = [];
}

/// <summary>
/// 集控广播命令：一次向多个班级（可按分组展开）发送通知、清除通知、电源或语音消息。
/// 逐班按发送者的班内有效权限鉴权并路由到各班插件，单班失败不影响其余班级。
/// </summary>
public sealed class BroadcastCommandRequest
{
    [JsonPropertyName("command")]
    public CommandKind Command { get; set; }

    /// <summary>SendNotification 命令的通知内容。</summary>
    [JsonPropertyName("notification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NotificationRequest? Notification { get; set; }

    /// <summary>Power 命令的动作（关机/重启/睡眠/休眠）。</summary>
    [JsonPropertyName("powerAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PowerActionKind? PowerAction { get; set; }

    /// <summary>SendVoiceMessage 命令的语音内容（base64 PCM）。</summary>
    [JsonPropertyName("voiceMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VoiceMessageRequest? VoiceMessage { get; set; }

    [JsonPropertyName("classIds")]
    public List<Guid> ClassIds { get; set; } = [];

    /// <summary>按分组广播：服务端展开为组内全部班级后与 classIds 合并去重。</summary>
    [JsonPropertyName("groupIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? GroupIds { get; set; }
}

public sealed class BatchClassItemResult
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
