namespace RemoteCI.Server.Data;

/// <summary>服务端档案模板或班级独立副本；ClassIsland 对象 ID 保留在完整 JSON 内。</summary>
public sealed class StoredProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新档案";
    public string ProfileJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public Guid? ClassId { get; set; }
    public Classroom? Classroom { get; set; }
    public Guid? SourceTemplateId { get; set; }
    public StoredProfile? SourceTemplate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
