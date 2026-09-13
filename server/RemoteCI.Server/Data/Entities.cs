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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PluginPairingCode
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
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

    /// <summary>启用后未登录访客可只读查看课表。</summary>
    public bool VisitorAccessEnabled { get; set; }

    /// <summary>启用访客功能时，访问 WebUI 落地页直接进入访客课表；关闭访客功能时无效。</summary>
    public bool AutoEnterVisitorPage { get; set; }
}

public enum AccountRoleKind
{
    Student = 1,
    Administrator = 2,
    Custom = 3,
}

public sealed class AccountRole
{
    public static readonly Guid StudentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AdministratorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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
