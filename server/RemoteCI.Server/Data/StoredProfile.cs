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

public enum ProfileVersionOrigin
{
    Save = 0,
    Copy = 1,
    Restore = 2,
    /// <summary>升级时为已有档案补的基线版本。</summary>
    Baseline = 3,
}

/// <summary>档案每次保存后的完整快照；每份档案保留最近 50 个，用于差异对比和回滚。</summary>
public sealed class StoredProfileVersion
{
    public const int MaxPerProfile = 50;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public StoredProfile Profile { get; set; } = null!;
    public long Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfileJson { get; set; } = "{}";
    public Guid? SavedByUserId { get; set; }
    public string SavedByName { get; set; } = string.Empty;
    public long SavedAtUnixMs { get; set; }
    public ProfileVersionOrigin Origin { get; set; }
}

/// <summary>某班教室电脑当前档案与服务端班级档案的最近一次比对结果。</summary>
public sealed class ProfileDriftReport
{
    public Guid ClassId { get; set; }
    public long CheckedAtUnixMs { get; set; }
    /// <summary>比对时服务端档案的修订号；档案再次保存后旧结果不再显示。</summary>
    public long ProfileRevision { get; set; }
    public int ChangeCount { get; set; }
    /// <summary>差异列表（ProfileChange 数组 JSON）。</summary>
    public string ChangesJson { get; set; } = "[]";
}
