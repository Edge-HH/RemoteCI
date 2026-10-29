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
/// 客户端一键打开 WebUI 的一次性网页登录票据。数据库只保存票据的 SHA-256 摘要，明文只出现在跳转链接中；
/// 持久化而非进程内缓存，服务重启或多实例共享数据库时仍可兑换，兑换时按行原子删除保证只能成功一次。
/// </summary>
public sealed class WebLoginTicket
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    /// <summary>签发时的安全戳：改密、停用等会刷新安全戳，使未兑换的票据失效。</summary>
    public string? SecurityStamp { get; set; }
    /// <summary>以 Unix 毫秒保存，SQLite 才能在 SQL 中直接比较并清理过期票据。</summary>
    public long ExpiresAtUnixMs { get; set; }
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
    /// <summary>归属班级；未分配的设备（统一连接码或尚未建班时的引导配对码接入）为 null。</summary>
    public Guid? ClassroomId { get; set; }
    public Classroom? Classroom { get; set; }
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

    /// <summary>配对成功后插件凭据归属的班级；统一连接码与引导配对码为 null，接入的设备进入未分配列表。</summary>
    public Guid? ClassroomId { get; set; }
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

    /// <summary>学期开始日期，用于教学周与单双周/多周轮换；null 表示未设置。</summary>
    public DateOnly? SemesterStartDate { get; set; }

    /// <summary>把学期开始日期写入教室端 ClassIsland 的单周起始日期；默认开启，仅在设置了学期开始日期后生效。</summary>
    public bool SemesterSyncToClassrooms { get; set; } = true;

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

    /// <summary>
    /// 手机扫码登录二维码中写入的服务器地址；null 表示使用当前 WebUI 访问地址。
    /// 只影响二维码内容，不改变服务端监听地址或反向代理配置。
    /// </summary>
    public string? MobileServerUrl { get; set; }

    /// <summary>浏览器 Web Push 的 VAPID 密钥对（Base64Url），首次启动自动生成；更换后旧订阅失效。</summary>
    public string? VapidPublicKey { get; set; }
    public string? VapidPrivateKey { get; set; }

    /// <summary>
    /// 旧版遗留“默认班级”的一次性升级是否已处理：未改名且已有其他班级时，其设备转入未分配并删除该班级；
    /// 否则保留为普通班级。全新部署直接视为已处理。
    /// </summary>
    public bool LegacyDefaultClassMigrated { get; set; }

    /// <summary>调休自动适配总开关：放假日关闭教室课表、调休上学日建立临时课表；默认开启。</summary>
    public bool HolidayCalendarEnabled { get; set; } = true;

    /// <summary>自定义节假日数据源地址模板（含 {year}）；null 表示使用内置的 holiday-cn 镜像。</summary>
    public string? HolidaySourceUrlTemplate { get; set; }

    /// <summary>命令审计记录保留天数（7-3650）；旧库升级后为 0，按默认 180 天处理。</summary>
    public int AuditRetentionDays { get; set; } = CommandAuditLimits.DefaultRetentionDays;

    // ---------- 设备离线告警 ----------

    /// <summary>教室电脑插件离线超过阈值时推送个人通知；默认开启。</summary>
    public bool DeviceAlertsEnabled { get; set; } = true;

    /// <summary>连续离线多少分钟后告警（5-120）。</summary>
    public int DeviceAlertThresholdMinutes { get; set; } = DeviceAlertDefaults.ThresholdMinutes;

    public bool DeviceAlertNotifyAdmins { get; set; } = true;

    public bool DeviceAlertNotifyClassHeads { get; set; } = true;

    /// <summary>只在班级当天的在校时段告警，避免放学关机后整晚报警。</summary>
    public bool DeviceAlertSchoolHoursOnly { get; set; } = true;

    // ---------- 档案差异检查 ----------

    /// <summary>每天定时读取在线教室电脑的档案，与服务端班级档案比对；默认开启。</summary>
    public bool ProfileDriftCheckEnabled { get; set; } = true;

    /// <summary>每日检查时刻（服务端本地时间 HH:mm），选在教室电脑通常开机的时段。</summary>
    public string ProfileDriftCheckTime { get; set; } = "12:30";

    /// <summary>最近一次自动检查的日期（服务端本地 yyyy-MM-dd），避免同一天重复检查。</summary>
    public string? ProfileDriftLastCheckDate { get; set; }

    /// <summary>组织名（如学校名称），用于应用守护恢复提示“当前ClassIsland由xx统一管理”；null 时显示 RemoteCI。</summary>
    public string? OrganizationName { get; set; }

    /// <summary>远程截图后是否在教室端弹出“已远程获取本机屏幕截图”的轻提示；默认静默。</summary>
    public bool ScreenshotNotifyClassroom { get; set; }

    // ---------- 服务端更新（部署相关，不随配置包迁移） ----------

    /// <summary>服务端更新渠道：手动检查与自动检查都按此渠道选版本。</summary>
    public RemoteCI.Server.Services.UpdateChannel UpdateChannel { get; set; } = RemoteCI.Server.Services.UpdateChannel.Stable;

    /// <summary>后台定期检查 GitHub Release 是否有新版本；默认开启。</summary>
    public bool UpdateAutoCheck { get; set; } = true;

    /// <summary>自动检查发现新版本后预先下载并校验更新包，管理员确认后直接安装；默认关闭。</summary>
    public bool UpdateAutoDownload { get; set; }

    // ---------- 应用守护（全局默认策略，单设备可在 AppGuardOverride 中覆盖） ----------

    public bool AppGuardEnabled { get; set; }

    public bool AppGuardRestartAfterExit { get; set; }

    /// <summary>正常退出后延时启动的分钟数（0-1440）；0 表示立即启动。</summary>
    public int AppGuardRestartDelayMinutes { get; set; }

    public bool AppGuardRestartOnCrash { get; set; } = true;

    public bool AppGuardPreventDeletion { get; set; }

    /// <summary>防删除定时备份间隔小时数（1-168）。</summary>
    public int AppGuardBackupIntervalHours { get; set; } = RemoteCI.Shared.Models.AppGuardPolicy.DefaultBackupIntervalHours;

    public bool AppGuardEnhancedMode { get; set; }

    public bool AppGuardRandomBackupPath { get; set; }

    public string AppGuardBackupDirectoryName { get; set; } = string.Empty;

    public string AppGuardProcessName { get; set; } = RemoteCI.Shared.Models.AppGuardPolicy.DefaultProcessName;

    public string AppGuardBackupPathsJson { get; set; } = "[]";
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
    /// <summary>
    /// 旧版本自动创建的“默认班级”的固定 Id。新部署不再有默认班级，首次进入 WebUI 时由系统管理员手动新建；
    /// 该 Id 只用于升级识别遗留班级与导入不含班级数据的旧配置包。
    /// </summary>
    public static readonly Guid LegacyDefaultId = Guid.Parse("33333333-3333-3333-3333-333333333333");

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
/// 扩展插件（扩展分组）的班级自治开关：开放后，本班班主任可以在侧栏“扩展插件”中自行修改本班设置。
/// 没有记录即不开放，新插件默认只由系统管理员统一管理。
/// </summary>
public sealed class ExtensionGroupPolicy
{
    public string GroupId { get; set; } = string.Empty;
    public bool AllowClassAdmin { get; set; }
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
