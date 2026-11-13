namespace RemoteCI.Server.Data;

/// <summary>
/// 聊天机器人执行账号/班级管理高危操作前签发的一次性网页授权令牌：绑定签发账号、动作与目标，
/// 数据库只保存令牌的 SHA-256 摘要与加密后的操作参数。打开链接只显示操作摘要，
/// 输入账号密码或一次性验证码授权后才执行；账号改密或停用（安全戳变化）后链接失效。
/// </summary>
public sealed class OperationApproval
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Action { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    /// <summary>操作参数（如新建/改密请求体）经 DataProtection 加密后的密文；删除类操作为空。</summary>
    public string ProtectedPayload { get; set; } = string.Empty;
    /// <summary>展示给授权者的操作摘要（不含密码等敏感字段）。</summary>
    public string Summary { get; set; } = string.Empty;
    public string? SecurityStamp { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; }
    public long? UsedAtUnixMs { get; set; }
}