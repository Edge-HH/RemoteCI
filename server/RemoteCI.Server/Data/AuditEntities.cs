namespace RemoteCI.Server.Data;

public static class CommandAuditLimits
{
    public const int TextLength = 500;
    public const int DefaultRetentionDays = 180;
}

/// <summary>命令从哪个入口发起；审计页据此区分 WebUI、手机、手表、API Key 与服务端自动流程。</summary>
public enum CommandAuditSource
{
    Unknown = 0,
    WebUi = 1,
    Mobile = 2,
    Watch = 3,
    ApiKey = 4,
    /// <summary>手机或手表 App 通过设备会话令牌调用 REST 接口。</summary>
    DeviceRest = 5,
    Automation = 6,
    /// <summary>换课申请审批通过或强制换课后，由服务端代为下发的换课命令。</summary>
    SwapWorkflow = 7,
    DriftCheck = 8,
    System = 9,
}

/// <summary>
/// 经服务端下发到教室端插件的一条命令。只保存脱敏摘要，不保存语音、文件内容或档案 JSON；
/// 时间用 Unix 毫秒存储，SQLite 才能按时间筛选与排序。
/// </summary>
public sealed class CommandAuditEntry
{
    public Guid Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
    public long? CompletedAtUnixMs { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public CommandAuditSource Source { get; set; }
    /// <summary>入口细节：API Key 名称、设备会话名称、自动化规则名或换课申请编号。</summary>
    public string? SourceDetail { get; set; }
    public Guid? ClassId { get; set; }
    public string? ClassName { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? DeviceName { get; set; }
    public int Command { get; set; }
    public string Summary { get; set; } = string.Empty;
    /// <summary>null 表示仍在等待插件回执。</summary>
    public bool? Success { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }
}
