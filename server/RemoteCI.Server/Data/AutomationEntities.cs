namespace RemoteCI.Server.Data;

/// <summary>
/// 服务端自动化规则：触发条件、目标班级与动作以 JSON 保存，结构见 <c>Services/AutomationModels.cs</c>。
/// 时间用 Unix 毫秒保存，SQLite 才能排序与筛选。
/// </summary>
public sealed class AutomationRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string TriggerJson { get; set; } = "{}";
    public string ActionsJson { get; set; } = "[]";
    public string TargetJson { get; set; } = "{}";
    /// <summary>生效起止日期（教室端本地日期，yyyy-MM-dd），为空表示不限。</summary>
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
    public long? LastRunAtUnixMs { get; set; }
    public bool? LastSuccess { get; set; }
    public string? LastResult { get; set; }
}

public enum AutomationRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>到期时服务端未运行或积压过久，按规定不再补跑。</summary>
    Missed = 4,
}

/// <summary>规则对某个班级的一次执行（含等待中的延迟任务）；DedupeKey 保证同一次触发只执行一次。</summary>
public sealed class AutomationRun
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public AutomationRule Rule { get; set; } = null!;
    public string RuleName { get; set; } = string.Empty;
    public Guid ClassId { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public string TriggerText { get; set; } = string.Empty;
    public long DueAtUnixMs { get; set; }
    public long? StartedAtUnixMs { get; set; }
    public long? FinishedAtUnixMs { get; set; }
    public AutomationRunStatus Status { get; set; }
    public string? Message { get; set; }
}
