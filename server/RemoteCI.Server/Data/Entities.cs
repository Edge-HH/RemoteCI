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

/// <summary>
/// 用户 API Key。密钥明文只在创建时展示一次，数据库只保存 SHA-256 摘要；
/// 权限不复制到密钥上，每次调用都重新按用户当前角色与授权计算。
/// </summary>
public sealed class UserApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    /// <summary>用于列表识别的前缀，不是完整密钥。</summary>
    public string Prefix { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
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
    /// <summary>统一连接码创建的凭据先保持未分配；分配后才可参与班级数据和命令路由。</summary>
    public bool Assigned { get; set; } = true;
    public string? ClassNameRemark { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>最近一次软件版本清单的 JSON 快照；用于设备离线时仍可查看最后版本。</summary>
    public string? SoftwareInventoryJson { get; set; }

    public DateTimeOffset? SoftwareInventoryAt { get; set; }
}

public sealed class PluginPairingCode
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>配对成功后插件凭据归属的班级；旧数据回填为默认班级。</summary>
    public Guid ClassroomId { get; set; } = Classroom.DefaultId;
    /// <summary>共享连接码可被无限次消费；班级配对码仍由 UsedAt 控制为一次性。</summary>
    public bool IsShared { get; set; }
    /// <summary>班级固定配对码：可重复使用，绑定到特定班级，班级创建/导入时由管理员指定。</summary>
    public bool IsPersistent { get; set; }
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

    /// <summary>登录页主题：管理员可强制浅色/深色，默认跟随访客本地偏好。</summary>
    public LoginTheme LoginTheme { get; set; } = LoginTheme.Follow;

    /// <summary>登录页背景图；null 表示使用默认网格背景。图片存库以便随配置一起备份与迁移。</summary>
    public byte[]? LoginBackground { get; set; }
    public string? LoginBackgroundContentType { get; set; }
    public DateTimeOffset? LoginBackgroundUpdatedAt { get; set; }

    /// <summary>登录页背景图不透明度百分比（0-100）；仅在设置了背景图时生效。</summary>
    public int LoginBackgroundOpacity { get; set; } = 100;

    /// <summary>登录页卡片在页面中的水平位置；默认居中。</summary>
    public LoginCardPosition LoginCardPosition { get; set; } = LoginCardPosition.Center;

    // ---------- 班级自治策略：系统管理员统一决定班主任可以在本班自行完成哪些操作 ----------

    /// <summary>班主任可以修改本班班名；默认允许，保持升级前行为。</summary>
    public bool ClassAdminCanRename { get; set; } = true;

    /// <summary>班主任可以上传或清除本班头像；默认允许，保持升级前行为。</summary>
    public bool ClassAdminCanChangeAvatar { get; set; } = true;

    /// <summary>班主任可以手动从插件拉取本班课表；默认允许，保持升级前行为。</summary>
    public bool ClassAdminCanPullSchedule { get; set; } = true;

    /// <summary>班主任可以修改本班设备上的扩展设置；默认关闭，扩展设置由系统管理员统一下发。</summary>
    public bool ClassAdminCanEditExtensionSettings { get; set; }

    /// <summary>
    /// 手机扫码登录二维码中写入的服务器地址；null 表示使用当前 WebUI 访问地址。
    /// 只影响二维码内容，不改变服务端监听地址或反向代理配置。
    /// </summary>
    public string? MobileServerUrl { get; set; }

    /// <summary>浏览器 Web Push 的 VAPID 密钥对（Base64Url），首次启动自动生成；更换后旧订阅失效。</summary>
    public string? VapidPublicKey { get; set; }
    public string? VapidPrivateKey { get; set; }
}

/// <summary>登录页主题策略：跟随访客本地偏好，或由管理员强制浅色/深色。</summary>
public enum LoginTheme
{
    Follow = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>登录页卡片的水平位置：左、中、右。</summary>
public enum LoginCardPosition
{
    Center = 0,
    Left = 1,
    Right = 2,
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
    /// <summary>
    /// 内置“班主任”（原“班管理员”）：仅在所属班级内生效的班级管理角色，不授予系统管理员身份；
    /// 同时与老师一样按显示名拥有个人“我的日程”。
    /// </summary>
    ClassAdministrator = 4,
    /// <summary>
    /// 内置“老师”：按显示名与课表科目教师名绑定任教班级，
    /// 默认仅授予任教班级的通知与语音消息权限，其余权限可在角色/人员设置中追加。
    /// </summary>
    Teacher = 5,
}

public sealed class AccountRole
{
    public static readonly Guid StudentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AdministratorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ClassAdministratorId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid TeacherId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>拥有个人“我的日程”（按显示名匹配课表科目教师名）的内置角色种类：老师与班主任。</summary>
    public static bool HasPersonalSchedule(AccountRoleKind kind) =>
        kind is AccountRoleKind.Teacher or AccountRoleKind.ClassAdministrator;

    /// <summary>按全局角色 Id 判断是否拥有个人“我的日程”；内置角色 Id 固定。</summary>
    public static bool HasPersonalSchedule(Guid roleId) => roleId == TeacherId || roleId == ClassAdministratorId;

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

/// <summary>
/// 下发时班级插件离线、尚未生效的扩展设置：插件重新上线并同步扩展分组后由服务端自动补发。
/// 同一班级同一分组只保留一条，多次修改按字段合并，后写入的值覆盖先写入的值。
/// </summary>
public sealed class PendingExtensionSetting
{
    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;
    public string GroupId { get; set; } = string.Empty;

    /// <summary>待写入字段的 JSON 对象（键为设置字段 Key，值统一为字符串）。</summary>
    public string ValuesJson { get; set; } = "{}";

    /// <summary>最近一次修改的账号；补发时以该账号在班级内的当前权限作为请求者。</summary>
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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
