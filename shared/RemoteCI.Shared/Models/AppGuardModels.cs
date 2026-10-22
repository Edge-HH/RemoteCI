using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 应用守护策略（<see cref="Protocol.MessageTypeAppGuardPolicy"/>）：由设备外的守护进程负责退出自启、意外退出重启与防删除恢复。
/// 服务端按“全局默认 + 单设备覆盖”算出每台设备的生效策略后下发。
/// </summary>
public sealed class AppGuardPolicy
{
    public const int MaxRestartDelayMinutes = 1440;
    public const int MinBackupIntervalHours = 1;
    public const int MaxBackupIntervalHours = 168;
    public const int DefaultBackupIntervalHours = 24;
    public const string DefaultOrganizationName = "RemoteCI";

    /// <summary>总开关；关闭后守护进程注销登录自启并退出，插件清理备份。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>ClassIsland 正常退出后自动启动。</summary>
    [JsonPropertyName("restartAfterExit")]
    public bool RestartAfterExit { get; set; }

    /// <summary>正常退出后等待多少分钟再启动；0 表示立即启动。</summary>
    [JsonPropertyName("restartAfterExitDelayMinutes")]
    public int RestartAfterExitDelayMinutes { get; set; }

    /// <summary>检测到 ClassIsland 意外退出（崩溃、被结束进程）时立即重新启动。</summary>
    [JsonPropertyName("restartOnCrash")]
    public bool RestartOnCrash { get; set; }

    /// <summary>禁止删除：定时备份 ClassIsland，发现被删除后自动恢复并启动。</summary>
    [JsonPropertyName("preventDeletion")]
    public bool PreventDeletion { get; set; }

    /// <summary>定时备份间隔（小时）。</summary>
    [JsonPropertyName("backupIntervalHours")]
    public int BackupIntervalHours { get; set; } = DefaultBackupIntervalHours;

    /// <summary>恢复后提示中显示的管理方（系统配置中的组织名）。</summary>
    [JsonPropertyName("organizationName")]
    public string OrganizationName { get; set; } = DefaultOrganizationName;

    public AppGuardPolicy Normalized() => new()
    {
        Enabled = Enabled,
        RestartAfterExit = RestartAfterExit,
        RestartAfterExitDelayMinutes = Math.Clamp(RestartAfterExitDelayMinutes, 0, MaxRestartDelayMinutes),
        RestartOnCrash = RestartOnCrash,
        PreventDeletion = PreventDeletion,
        BackupIntervalHours = Math.Clamp(BackupIntervalHours, MinBackupIntervalHours, MaxBackupIntervalHours),
        OrganizationName = string.IsNullOrWhiteSpace(OrganizationName) ? DefaultOrganizationName : OrganizationName.Trim(),
    };

    /// <summary>恢复 ClassIsland 后在新实例中弹出的提示文字。</summary>
    public static string RestoredNotice(string? organizationName) =>
        $"当前ClassIsland由{(string.IsNullOrWhiteSpace(organizationName) ? DefaultOrganizationName : organizationName.Trim())}统一管理，请勿自行删除，当前实例已由RemoteCI自动恢复。";

    /// <summary>面向管理员和设置页的一句话摘要。</summary>
    public string Describe()
    {
        if (!Enabled) return "未启用";
        var parts = new List<string>();
        if (RestartAfterExit)
            parts.Add(RestartAfterExitDelayMinutes > 0 ? $"退出 {RestartAfterExitDelayMinutes} 分钟后自启" : "退出后立即自启");
        if (RestartOnCrash) parts.Add("意外退出时重启");
        if (PreventDeletion) parts.Add("禁止删除");
        return parts.Count == 0 ? "已启用（未开启任何守护项）" : string.Join(" / ", parts);
    }
}

/// <summary>守护进程记录、由插件上报的事件类型。</summary>
public static class AppGuardEventKinds
{
    /// <summary>检测到 ClassIsland 被删除，已从备份恢复并启动。</summary>
    public const string Restored = "restored";
    /// <summary>检测到被删除，但恢复失败。</summary>
    public const string RestoreFailed = "restore-failed";
    /// <summary>意外退出后已重新启动。</summary>
    public const string CrashRestarted = "crash-restarted";
    /// <summary>正常退出并到达延时后已自动启动。</summary>
    public const string ExitRestarted = "exit-restarted";
    /// <summary>短时间内反复崩溃，暂停自动重启。</summary>
    public const string CrashLoopPaused = "crash-loop-paused";
    public const string BackupCompleted = "backup-completed";
    public const string BackupFailed = "backup-failed";

    public static string ChineseName(string? kind) => kind switch
    {
        Restored => "已自动恢复",
        RestoreFailed => "恢复失败",
        CrashRestarted => "意外退出后重启",
        ExitRestarted => "退出后自启",
        CrashLoopPaused => "频繁崩溃，暂停重启",
        BackupCompleted => "备份完成",
        BackupFailed => "备份失败",
        _ => kind ?? "未知事件",
    };
}

public sealed class AppGuardEvent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("atUnixMs")]
    public long AtUnixMs { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

/// <summary>插件上报给服务端的守护事件（<see cref="Protocol.MessageTypeAppGuardEvent"/>）。</summary>
public sealed class AppGuardEventBatch
{
    [JsonPropertyName("events")]
    public List<AppGuardEvent> Events { get; set; } = [];
}

/// <summary>GET /api/devices/{credentialId}/app-guard 的响应。</summary>
public sealed class AppGuardDevicePolicyView
{
    [JsonPropertyName("credentialId")]
    public Guid CredentialId { get; set; }

    /// <summary>是否使用单设备覆盖；false 表示沿用全局默认。</summary>
    [JsonPropertyName("overridden")]
    public bool Overridden { get; set; }

    [JsonPropertyName("policy")]
    public AppGuardPolicy Policy { get; set; } = new();
}

/// <summary>GET /api/app-guard/events 的一条记录。</summary>
public sealed class AppGuardEventView
{
    [JsonPropertyName("credentialId")]
    public Guid CredentialId { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("kindName")]
    public string KindName { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("atUnixMs")]
    public long AtUnixMs { get; set; }
}
