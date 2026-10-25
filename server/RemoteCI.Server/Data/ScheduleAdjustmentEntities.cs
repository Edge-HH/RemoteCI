namespace RemoteCI.Server.Data;

public enum ScheduleAdjustmentOrigin
{
    /// <summary>换课申请（审批通过、两节都是自己的课或强制换课）。</summary>
    SwapRequest = 1,
    /// <summary>经 RemoteCI 换课命令直接修改（课表页、手机、手表、局域网），由插件上报后收编。</summary>
    Direct = 2,
    /// <summary>在 ClassIsland 内直接修改临时层，由插件上报后收编。</summary>
    ClassIsland = 3,
    /// <summary>升级前已生效的换课，从旧的 AppliedPlanJson 回填。</summary>
    Backfill = 4,
}

public enum ScheduleAdjustmentStatus
{
    Active = 1,
    /// <summary>被撤回（撤回强制换课、班主任撤回、删除调课记录）。</summary>
    Reverted = 2,
    /// <summary>教室端无法应用（基础课表变化、放假、插件过旧等），见 ConflictCode。</summary>
    Invalidated = 3,
    /// <summary>日期已过。</summary>
    Expired = 4,
}

public enum ScheduleAdjustmentDelivery
{
    /// <summary>等待教室端回执。</summary>
    Pending = 0,
    Applied = 1,
    Conflict = 2,
}

/// <summary>
/// 调课记录：某班某日某节从基础科目 FromSubject 改为 ToSubject，是单日调整的权威来源。
/// 同一课位可叠放多条，Sequence 最大的有效记录生效；撤回上层后回落到下层。
/// FromSubjectId 是该节基础课表应有的科目（守卫），同一课位所有记录相同；基础课表变化后守卫对不上，记录失效。
/// 教室端的临时层由插件按“基础课表 + 调课记录”生成，见 docs/adr/0001-timetable-source-of-truth.md。
/// </summary>
public sealed class ScheduleAdjustment
{
    public Guid Id { get; set; }
    public Guid ClassId { get; set; }
    public string Date { get; set; } = string.Empty;
    public int Index { get; set; }
    /// <summary>班级内单调递增的叠放顺序，见 <see cref="ClassSyncState.AdjustmentSequence"/>。</summary>
    public long Sequence { get; set; }

    public Guid FromSubjectId { get; set; }
    public string FromSubject { get; set; } = string.Empty;
    public Guid ToSubjectId { get; set; }
    public string ToSubject { get; set; } = string.Empty;
    /// <summary>该节实际任课老师与新科目的默认老师不同时的临时任课老师（ClassIsland 只有“科目→老师”）。</summary>
    public string? TeacherName { get; set; }

    public ScheduleAdjustmentOrigin Origin { get; set; }
    public Guid? SwapRequestId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }

    public ScheduleAdjustmentStatus Status { get; set; } = ScheduleAdjustmentStatus.Active;
    public ScheduleAdjustmentDelivery Delivery { get; set; }
    public string? ConflictCode { get; set; }
    public string? StatusReason { get; set; }
    public long? ClosedAtUnixMs { get; set; }
}

/// <summary>班级与教室端之间的同步状态；阶段二会在这里加入档案部署信息。</summary>
public sealed class ClassSyncState
{
    public Guid ClassId { get; set; }
    /// <summary>调课集合版本，每次该班有效调课变化时递增。</summary>
    public long AdjustmentVersion { get; set; }
    /// <summary>调课叠放序号的最大值。</summary>
    public long AdjustmentSequence { get; set; }
    /// <summary>插件最后回执的调课集合版本。</summary>
    public long AckedAdjustmentVersion { get; set; }
    public long? AckedAtUnixMs { get; set; }
}
