using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 服务端下发给插件的调课集合（<see cref="Protocol.MessageTypeScheduleAdjustments"/>）：该班今天起全部有效调课，
/// 每个课位只有最上层一条。插件按“基础课表 + 调课”生成托管临时层，集合是期望状态，可重复下发。
/// </summary>
public sealed class ScheduleAdjustmentSet
{
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    /// <summary>该班调课集合的版本号，每次变化递增；插件在课表回执中报告已应用的版本。</summary>
    [JsonPropertyName("version")]
    public long Version { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("items")]
    public List<ScheduleSlotChange> Items { get; set; } = [];

    /// <summary>服务端已经记录的插件本地调课 ID（无论之后状态如何），插件据此把它们移出待上报列表。</summary>
    [JsonPropertyName("acknowledgedLocalIds")]
    public List<Guid> AcknowledgedLocalIds { get; set; } = [];
}

/// <summary>一个课位的调课：<see cref="FromSubjectId"/> 是该节基础课表应有的科目（守卫），对不上即冲突。</summary>
public sealed class ScheduleSlotChange
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("fromSubjectId")]
    public Guid FromSubjectId { get; set; }

    [JsonPropertyName("toSubjectId")]
    public Guid ToSubjectId { get; set; }
}

/// <summary>插件随 <see cref="ScheduleBundle"/> 上报的调课回执。</summary>
public sealed class ScheduleAdjustmentReport
{
    /// <summary>插件最后应用的服务端调课集合版本；从未收到集合时为 0。</summary>
    [JsonPropertyName("version")]
    public long Version { get; set; }

    /// <summary>服务端条目的处理结果。</summary>
    [JsonPropertyName("items")]
    public List<ScheduleAdjustmentItemResult> Items { get; set; } = [];

    /// <summary>插件本地产生、尚未被服务端确认的调课（局域网换课、ClassIsland 内直接修改临时层等）。</summary>
    [JsonPropertyName("local")]
    public List<LocalScheduleAdjustment> Local { get; set; } = [];

    /// <summary>存在插件无法托管的临时层（与基础课表节数不同等）的日期，这些日期的服务端调课以冲突返回。</summary>
    [JsonPropertyName("unmanagedDates")]
    public List<string> UnmanagedDates { get; set; } = [];
}

public enum ScheduleAdjustmentItemState
{
    Pending = 0,
    Applied = 1,
    Conflict = 2,
}

public sealed class ScheduleAdjustmentItemResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("state")]
    public ScheduleAdjustmentItemState State { get; set; }

    /// <summary>冲突码，见 <see cref="ScheduleAdjustmentConflictCodes"/>。</summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>插件本地产生的调课；ID 由插件生成，服务端按 ID 幂等收编。</summary>
public sealed class LocalScheduleAdjustment
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("fromSubjectId")]
    public Guid FromSubjectId { get; set; }

    [JsonPropertyName("toSubjectId")]
    public Guid ToSubjectId { get; set; }

    /// <summary>来源，见 <see cref="ScheduleAdjustmentOrigins"/>。</summary>
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = ScheduleAdjustmentOrigins.Direct;

    [JsonPropertyName("requestedByUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RequestedByUserId { get; set; }

    [JsonPropertyName("requestedByName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedByName { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>GET /api/classes/{classId}/schedule-adjustments 的列表项。</summary>
public sealed class ScheduleAdjustmentView
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("fromSubject")]
    public string FromSubject { get; set; } = string.Empty;

    [JsonPropertyName("toSubject")]
    public string ToSubject { get; set; } = string.Empty;

    [JsonPropertyName("teacher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Teacher { get; set; }

    /// <summary>swap / direct / classisland / backfill。</summary>
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = string.Empty;

    /// <summary>active / reverted / invalidated / expired。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>pending / applied / conflict。</summary>
    [JsonPropertyName("delivery")]
    public string Delivery { get; set; } = string.Empty;

    /// <summary>当前是否为该课位生效的（叠放最上层的）有效记录。</summary>
    [JsonPropertyName("effective")]
    public bool Effective { get; set; }

    [JsonPropertyName("conflictCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConflictCode { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("swapRequestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SwapRequestId { get; set; }

    [JsonPropertyName("createdByName")]
    public string CreatedByName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ScheduleAdjustmentOrigins
{
    /// <summary>经 RemoteCI 换课命令（网页课表页、手机、手表、局域网）直接修改的临时换课。</summary>
    public const string Direct = "direct";
    /// <summary>在 ClassIsland 内直接修改临时层。</summary>
    public const string ClassIsland = "classisland";
}

public static class ScheduleAdjustmentConflictCodes
{
    /// <summary>该节基础课表的科目已不是调课记录的原科目（档案或时间表变化）。</summary>
    public const string BaseChanged = "BASE_CHANGED";
    public const string IndexOutOfRange = "INDEX_OUT_OF_RANGE";
    /// <summary>该日没有可用课表。</summary>
    public const string NoPlan = "NO_PLAN";
    /// <summary>该日已改为放假。</summary>
    public const string Holiday = "HOLIDAY";
    /// <summary>该日有插件无法托管的临时层。</summary>
    public const string ForeignOverlay = "FOREIGN_OVERLAY";
    /// <summary>要换入的科目在设备档案中不存在。</summary>
    public const string SubjectMissing = "SUBJECT_MISSING";
    /// <summary>教室端插件版本过旧，不支持按调课记录生成临时层。</summary>
    public const string PluginOutdated = "PLUGIN_OUTDATED";
    /// <summary>ClassIsland 保存失败。</summary>
    public const string SaveFailed = "SAVE_FAILED";

    public static string Describe(string? code) => code switch
    {
        BaseChanged => "该节的基础课表已变化",
        IndexOutOfRange => "该日课表已没有这一节",
        NoPlan => "该日没有可用课表",
        Holiday => "该日已改为放假",
        ForeignOverlay => "该日有教室端无法接管的临时层",
        SubjectMissing => "教室端档案中没有要换入的科目",
        PluginOutdated => "教室端插件版本过旧",
        SaveFailed => "教室端保存课表失败",
        _ => "教室端无法应用这条调课",
    };
}

/// <summary>调课组合的纯计算，插件生成临时层与测试共用。</summary>
public static class ScheduleAdjustmentMath
{
    public sealed record ComposeResult(
        IReadOnlyList<Guid> Subjects,
        IReadOnlyDictionary<int, Guid> AppliedIds,
        IReadOnlyList<ScheduleAdjustmentItemResult> Results);

    /// <summary>
    /// 在基础科目上依次叠加服务端条目与本地条目（本地条目较新，在上层）。守卫不符的服务端条目报告冲突，
    /// 守卫不符的本地条目直接跳过（它们会在被服务端收编后以冲突形式出现）。
    /// </summary>
    public static ComposeResult Compose(
        IReadOnlyList<Guid> baseSubjects,
        IEnumerable<ScheduleSlotChange> server,
        IEnumerable<LocalScheduleAdjustment> local,
        Func<Guid, bool> subjectExists)
    {
        ArgumentNullException.ThrowIfNull(baseSubjects);
        var subjects = baseSubjects.ToList();
        var applied = new Dictionary<int, Guid>();
        var results = new List<ScheduleAdjustmentItemResult>();
        foreach (var item in server)
        {
            if (Check(baseSubjects, item.Index, item.FromSubjectId, item.ToSubjectId, subjectExists) is { } code)
            {
                results.Add(Conflict(item.Id, code));
                continue;
            }
            subjects[item.Index] = item.ToSubjectId;
            applied[item.Index] = item.Id;
            results.Add(new ScheduleAdjustmentItemResult { Id = item.Id, State = ScheduleAdjustmentItemState.Applied });
        }
        foreach (var item in local)
        {
            if (Check(baseSubjects, item.Index, item.FromSubjectId, item.ToSubjectId, subjectExists) is not null) continue;
            subjects[item.Index] = item.ToSubjectId;
            applied[item.Index] = item.Id;
        }
        return new ComposeResult(subjects, applied, results);
    }

    public static ScheduleAdjustmentItemResult Conflict(Guid id, string code) => new()
    {
        Id = id,
        State = ScheduleAdjustmentItemState.Conflict,
        Code = code,
        Message = ScheduleAdjustmentConflictCodes.Describe(code),
    };

    /// <summary>
    /// 把一次临时换课请求换算成逐节调课：<paramref name="effective"/> 为当前生效科目，<paramref name="baseSubjects"/>
    /// 为基础科目（守卫）。只返回生效科目真正变化的节次；请求本身的合法性由 ScheduleMutation.Validate 预先校验。
    /// </summary>
    public static IReadOnlyList<(int Index, Guid From, Guid To)> FromChangeRequest(
        ScheduleChangeRequest request, IReadOnlyList<Guid> effective, IReadOnlyList<Guid> baseSubjects)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (effective.Count != baseSubjects.Count) throw new ArgumentException("生效课表与基础课表节数不同");
        var next = effective.ToList();
        if (request.Mode == ScheduleChangeMode.Exchange)
        {
            var target = request.TargetIndex ?? throw new ArgumentException("互换需要目标节次");
            (next[request.SourceIndex], next[target]) = (next[target], next[request.SourceIndex]);
        }
        else
        {
            next[request.SourceIndex] = request.ReplacementSubjectId ?? throw new ArgumentException("替换需要新科目");
        }
        return Enumerable.Range(0, next.Count)
            .Where(index => next[index] != effective[index])
            .Select(index => (index, baseSubjects[index], next[index]))
            .ToList();
    }

    private static string? Check(IReadOnlyList<Guid> baseSubjects, int index, Guid from, Guid to, Func<Guid, bool> subjectExists)
    {
        if (index < 0 || index >= baseSubjects.Count) return ScheduleAdjustmentConflictCodes.IndexOutOfRange;
        if (baseSubjects[index] != from) return ScheduleAdjustmentConflictCodes.BaseChanged;
        if (to != from && !subjectExists(to)) return ScheduleAdjustmentConflictCodes.SubjectMissing;
        return null;
    }
}
