namespace RemoteCI.Plugin.Services;

/// <summary>插件到 RemoteCI 云端服务端的当前连接阶段。</summary>
public enum CloudConnectionState
{
    Stopped,
    Disabled,
    Connecting,
    Connected,
    WaitingForCredentials,
    WaitingToRetry,
}

/// <summary>
/// 设置页可安全展示的连接快照。
/// Error 只包含脱敏后的诊断信息；完整异常仍写入 ClassIsland 日志。
/// </summary>
public sealed record CloudConnectionStatus(
    CloudConnectionState State,
    string Summary,
    string? Error,
    DateTimeOffset ChangedAt)
{
    public bool IsConnected => State == CloudConnectionState.Connected;

    public static CloudConnectionStatus Stopped() => new(
        CloudConnectionState.Stopped,
        "RemoteCI 服务尚未启动",
        null,
        DateTimeOffset.UtcNow);
}

/// <summary>一次由设置页发起的真实云端连接测试结果。</summary>
public sealed record CloudConnectionTestResult(
    bool Success,
    string Message,
    CloudConnectionStatus Status);
