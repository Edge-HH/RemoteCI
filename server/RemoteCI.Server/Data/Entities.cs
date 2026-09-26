namespace RemoteCI.Server.Data;

public sealed class DeviceSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string DeviceName { get; set; } = string.Empty;
    public string VerifierHash { get; set; } = string.Empty;
    public string AccessTokenHash { get; set; } = string.Empty;
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class PluginCredential
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Guid ClassroomId { get; set; } = Classroom.DefaultId;
    public Classroom Classroom { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PluginPairingCode
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>配对成功后插件凭据归属的班级；旧数据回填为默认班级。</summary>
    public Guid ClassroomId { get; set; } = Classroom.DefaultId;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class SystemMetadata
{
    public int Id { get; set; } = 1;
    public long AccountVersion { get; set; }

    /// <summary>全局通知设置：开启后所有通知标题强制添加“由用户名发送：”前缀。</summary>
    public bool ForceSenderInTitle { get; set; } = true;

    /// <summary>服务端主动向插件拉取课表的间隔分钟数；0 表示关闭定时拉取。</summary>
    public int SchedulePullIntervalMinutes { get; set; }

    /// <summary>启用访客功能时，访问 WebUI 落地页直接进入访客课表；没有任何班级启用访客功能时无效。</summary>
    public bool AutoEnterVisitorPage { get; set; }
}

/// <summary>班级：一个班级对应一台教室端 ClassIsland 插件与其课表/状态流。</summary>
public sealed class Classroom
{
    /// <summary>升级自单班级版本的部署统一落到默认班级；不可删除。</summary>
    public static readonly Guid DefaultId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>启用后未登录访客可只读查看本班课表。</summary>
    public bool VisitorAccessEnabled { get; set; }

    /// <summary>班级头像（小尺寸图片，≤256KB）；null 表示未设置。</summary>
    public byte[]? Avatar { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTimeOffset? AvatarUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ClassMembership> Memberships { get; set; } = [];
    public ICollection<ClassGroupAssignment> GroupAssignments { get; set; } = [];
}

/// <summary>
/// 班级分组：管理员组织班级的维度（如年级、校区），支持父子层级；
/// 批量操作与广播通知按组展开时包含全部子分组中的班级。一个班级可属于多个分组。
/// </summary>
public sealed class ClassGroup
{
    /// <summary>分组最大层级深度：根为 1，超出后不能再建子分组。</summary>
    public const int MaxDepth = 4;

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>父分组；null 表示根分组。</summary>
    public Guid? ParentId { get; set; }
    public ClassGroup? Parent { get; set; }
    public ICollection<ClassGroup> Children { get; set; } = [];
    public ICollection<ClassGroupAssignment> Assignments { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>班级与分组的多对多归属；一个班级可同时属于多个分组。</summary>
public sealed class ClassGroupAssignment
{
    public Guid GroupId { get; set; }
    public ClassGroup Group { get; set; } = null!;
    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;
}

/// <summary>用户在某个班级中的成员关系与班内角色；一个用户可属于多个班级。</summary>
public sealed class ClassMembership
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;
    public Guid RoleDefinitionId { get; set; }
    public AccountRole RoleDefinition { get; set; } = null!;
}

public enum AccountRoleKind
{
    Student = 1,
    Administrator = 2,
    Custom = 3,
    /// <summary>内置“班管理员”：仅在所属班级内生效的班级管理角色，不授予系统管理员身份。</summary>
    ClassAdministrator = 4,
}

public sealed class AccountRole
{
    public static readonly Guid StudentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AdministratorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ClassAdministratorId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public AccountRoleKind Kind { get; set; }
    public RemoteCI.Shared.UserPermissions DefaultPermissions { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<AppUser> Users { get; set; } = [];
}

/// <summary>管理员为插件扩展设置的服务端全局调用策略。</summary>
public sealed class ExtensionPolicy
{
    public string ExtensionId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AllowNonAdmin { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>账号是否在自己的手表上展示某个扩展；没有记录时默认展示。</summary>
public sealed class UserExtensionPreference
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string ExtensionId { get; set; } = string.Empty;
    public bool ShowOnWatch { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum BackupCadence
{
    Hourly = 1,
    Daily = 2,
    Weekly = 3,
}

public sealed class BackupConfiguration
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public BackupCadence Cadence { get; set; } = BackupCadence.Daily;
    public TimeSpan TimeOfDay { get; set; } = TimeSpan.FromHours(2);
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public int MaxBackups { get; set; } = 7;
    public DateTimeOffset? LastScheduledAt { get; set; }
    public DateTimeOffset? LastSucceededAt { get; set; }
    public string? LastError { get; set; }
}
