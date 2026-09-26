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

    /// <summary>该用户在本班级内的有效权限（按成员角色计算）。</summary>
    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserPermissions? Permissions { get; set; }

    [JsonPropertyName("visitorEnabled")]
    public bool VisitorEnabled { get; set; }
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

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
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

    /// <summary>enableVisitor / disableVisitor / delete。</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;
}

public sealed class BatchClassOperationResult
{
    [JsonPropertyName("results")]
    public List<BatchClassItemResult> Results { get; set; } = [];
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
