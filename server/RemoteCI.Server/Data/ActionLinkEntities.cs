namespace RemoteCI.Server.Data;

/// <summary>
/// 聊天卡片按钮使用的一次性签名链接：绑定签发账号、动作与目标，数据库只保存令牌的 SHA-256 摘要。
/// 打开链接只显示确认页，确认提交后才执行；账号改密或停用（安全戳变化）后链接失效。
/// </summary>
public sealed class ActionLinkToken
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Action { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public string? SecurityStamp { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; }
    public long? UsedAtUnixMs { get; set; }
}
