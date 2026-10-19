namespace RemoteCI.Server.Data;

/// <summary>
/// 电子班牌链接：教室门口的班牌屏或走廊大屏凭私密链接匿名查看本班课表与当前课程。
/// 数据库只保存令牌摘要，链接只在创建时显示一次；撤销后立即失效。
/// </summary>
public sealed class ClassBoard
{
    public Guid Id { get; set; }
    public Guid ClassId { get; set; }
    public Classroom Classroom { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    /// <summary>是否在班牌上显示任课老师姓名。</summary>
    public bool ShowTeachers { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long? LastSeenAtUnixMs { get; set; }
}
