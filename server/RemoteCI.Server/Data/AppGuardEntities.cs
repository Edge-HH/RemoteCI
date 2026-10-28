namespace RemoteCI.Server.Data;

/// <summary>
/// 单台设备的应用守护策略覆盖：存在这一行时该设备使用这里的完整策略，否则沿用 <see cref="SystemMetadata"/> 中的全局默认。
/// 随插件凭据一起级联删除。
/// </summary>
public sealed class AppGuardOverride
{
    public Guid CredentialId { get; set; }
    public PluginCredential Credential { get; set; } = null!;
    public bool Enabled { get; set; }
    public bool RestartAfterExit { get; set; }
    public int RestartDelayMinutes { get; set; }
    public bool RestartOnCrash { get; set; }
    public bool PreventDeletion { get; set; }
    public int BackupIntervalHours { get; set; } = RemoteCI.Shared.Models.AppGuardPolicy.DefaultBackupIntervalHours;
    public bool EnhancedMode { get; set; }
    public bool RandomBackupPath { get; set; }
    public string BackupDirectoryName { get; set; } = string.Empty;
    public string ProcessName { get; set; } = RemoteCI.Shared.Models.AppGuardPolicy.DefaultProcessName;
    public string BackupPathsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>插件上报的守护事件（自动恢复、崩溃重启、备份结果等），保留 90 天。</summary>
public sealed class AppGuardEventRecord
{
    public long Id { get; set; }
    public Guid CredentialId { get; set; }
    public Guid? ClassId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public long AtUnixMs { get; set; }
    public long ReceivedAtUnixMs { get; set; }
}
