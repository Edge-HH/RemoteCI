namespace RemoteCI.Server.Data;

/// <summary>插件凭据的一次上线或离线；设备状态页据此画出当天的在线时间条，保留 30 天。</summary>
public sealed class DevicePresenceEvent
{
    public long Id { get; set; }
    public Guid CredentialId { get; set; }
    public Guid? ClassId { get; set; }
    public bool Online { get; set; }
    public long AtUnixMs { get; set; }
}

/// <summary>
/// 设备当前这一次离线的告警进度；设备恢复在线后删除。落库是为了服务端重启后
/// 不重复告警，也不漏发恢复通知。
/// </summary>
public sealed class DeviceAlertState
{
    public Guid CredentialId { get; set; }
    public long OfflineSinceUnixMs { get; set; }
    public long? AlertedAtUnixMs { get; set; }
}

public static class DeviceAlertDefaults
{
    public const int ThresholdMinutes = 10;
    public const int MinThresholdMinutes = 5;
    public const int MaxThresholdMinutes = 120;
}
