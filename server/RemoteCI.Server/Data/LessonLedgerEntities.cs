namespace RemoteCI.Server.Data;

public enum LessonLedgerSource
{
    /// <summary>教室端插件同步的实际课表（含换课与临时任课老师）。</summary>
    ClassIsland = 0,
    /// <summary>插件从未同步过课表，按服务端班级档案推算。</summary>
    Profile = 1,
}

/// <summary>
/// 课时台账：每节课结束后记一行实际由谁上课，之后不再修改。换课产生的临时任课老师覆盖在日期过去后会被清理，
/// 只有台账保留了“实际谁上的课”。服务端停机期间结束的课无法补录。
/// </summary>
public sealed class LessonLedgerEntry
{
    public long Id { get; set; }
    public Guid ClassId { get; set; }
    public string ClassName { get; set; } = string.Empty;
    /// <summary>教室端本地日期 yyyy-MM-dd。</summary>
    public string Date { get; set; } = string.Empty;
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    /// <summary>实际上课老师（教师字段原文，可能是“张三/李四”）。</summary>
    public string? TeacherName { get; set; }
    /// <summary>该科目的默认任课老师；代课时与实际老师不同。</summary>
    public string? DefaultTeacherName { get; set; }
    public bool IsSubstitute { get; set; }
    public LessonLedgerSource Source { get; set; }
    public long RecordedAtUnixMs { get; set; }
}
