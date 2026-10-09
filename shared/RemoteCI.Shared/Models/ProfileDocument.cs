using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 不依赖 ClassIsland UI 程序集的档案边界。编辑和分发均保留原始 JSON 节点，
/// 不把已知字段投影成不完整 DTO，以免丢失插件附加配置及较新宿主的字段。
/// </summary>
public static class ProfileDocument
{
    public const int MaxUtf8Bytes = 5 * 1024 * 1024;
    /// <summary>ClassIsland 内置“默认”课表群，档案中不一定显式存在。</summary>
    private static readonly Guid DefaultClassPlanGroupId = Guid.Parse("acaf4ef0-e261-4262-b941-34ea93cb4369");
    private const ProfileDistributionSection AllSections = ProfileDistributionSection.TimeLayouts |
        ProfileDistributionSection.ClassPlans | ProfileDistributionSection.Subjects;
    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        // This JSON is a storage/protocol payload, never an inline HTML script. Keep UTF-8
        // characters literal so a valid 5 MB upload does not expand through \uXXXX escaping.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonObject profile) => profile.ToJsonString(StorageOptions);

    public static JsonObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("档案 JSON 不能为空");
        if (Encoding.UTF8.GetByteCount(json) > MaxUtf8Bytes) throw new ArgumentException("档案 JSON 不能超过 5 MB");
        try
        {
            var root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject
                ?? throw new ArgumentException("档案 JSON 根节点必须是对象");
            // Keep unknown metadata case-sensitive. Known ClassIsland fields are resolved below
            // without case sensitivity, while ambiguous duplicates are rejected explicitly.
            Materialize(root);
            return root;
        }
        catch (JsonException ex) { throw new ArgumentException($"档案 JSON 无效：{ex.Message}", ex); }
    }

    public static IReadOnlyList<string> Validate(string json)
    {
        try { return Validate(Parse(json)); }
        catch (ArgumentException ex) { return [ex.Message]; }
    }

    public static IReadOnlyList<string> Validate(JsonObject profile)
    {
        var errors = new List<string>();
        CheckKnownFields(profile, ["Name", "Id", "TimeLayouts", "ClassPlans", "Subjects", "ClassPlanGroups",
            "OrderedSchedules", "OverlayClassPlanId", "TempClassPlanId", "IsOverlayClassPlanEnabled", "IsTempClassPlanGroupEnabled"], errors);
        CheckTypes(profile, "档案", errors, ["Name"], ["IsOverlayClassPlanEnabled", "IsTempClassPlanGroupEnabled"]);
        CheckGuidValue(profile, "Id", "档案", errors);
        var layouts = ReadDictionary(profile, "TimeLayouts", errors);
        var plans = ReadDictionary(profile, "ClassPlans", errors);
        var subjects = ReadDictionary(profile, "Subjects", errors);
        var groups = ReadDictionary(profile, "ClassPlanGroups", errors, allowEmptyIds: true);
        foreach (var (id, node) in subjects)
            if (node is JsonObject subject)
                CheckTypes(subject, $"科目“{id}”", errors, ["Name", "Initial", "TeacherName"], ["IsOutDoor"]);
        foreach (var (id, node) in groups)
            if (node is JsonObject group)
                CheckTypes(group, $"课表群“{id}”", errors, ["Name"], ["IsGlobal"]);
        foreach (var (id, node) in layouts)
        {
            if (node is not JsonObject layout) continue;
            var prefix = $"时间表“{Text(layout, "Name") ?? id}”";
            CheckTypes(layout, prefix, errors, ["Name"], ["IsActivated", "IsActivatedManually", "IsOverlay"]);
            CheckReference(layout, "OverlaySourceId", layouts, prefix + "临时层源时间表", errors, true);
            if (Get(layout, "Layouts") is not JsonArray points)
            {
                errors.Add($"{prefix}缺少时间点数组 Layouts");
                continue;
            }
            foreach (var (point, index) in points.Select((point, index) => (point, index)))
            {
                var position = $"{prefix}第 {index + 1} 个时间点";
                if (point is not JsonObject item) { errors.Add($"{position}必须是对象"); continue; }
                CheckKnownFields(item, ["StartTime", "EndTime", "StartSecond", "EndSecond", "TimeType", "DefaultClassId"], errors);
                CheckTypes(item, position, errors, ["BreakName"], ["IsHideDefault"]);
                var type = Integer(item, "TimeType", 0);
                if (type is < 0 or > 3) errors.Add($"{position}类型必须是 0、1、2 或 3");
                if (!TryTime(item, "StartTime", "StartSecond", out var start) ||
                    !TryTime(item, "EndTime", "EndSecond", out var end) ||
                    start < TimeSpan.Zero || end >= TimeSpan.FromDays(1) || end < start)
                    errors.Add($"{position}的开始或结束时间无效");
                CheckReference(item, "DefaultClassId", subjects, position + "默认科目", errors, true);
            }
        }
        foreach (var (id, node) in plans)
        {
            if (node is not JsonObject plan) continue;
            var prefix = $"课表“{Text(plan, "Name") ?? id}”";
            CheckTypes(plan, prefix, errors, ["Name"], ["IsEnabled", "IsOverlay", "IsActivated"]);
            if (Has(plan, "AssociatedGroup"))
            {
                var groupText = Text(plan, "AssociatedGroup");
                if (!Guid.TryParse(groupText, out var groupId)) errors.Add($"{prefix}课表群 ID 无效");
                else if (groupId != Guid.Empty && groupId != DefaultClassPlanGroupId &&
                         FindByGuid(groups, groupText!) is null) errors.Add($"{prefix}引用了不存在的课表群“{groupId}”");
            }
            var layoutId = CheckReference(plan, "TimeLayoutId", layouts, prefix + "时间表", errors, false);
            if (Get(plan, "Classes") is not JsonArray classes)
                errors.Add($"{prefix}缺少课程数组 Classes");
            else
            {
                foreach (var (lesson, index) in classes.Select((lesson, index) => (lesson, index)))
                {
                    if (lesson is JsonObject lessonObject)
                    {
                        CheckTypes(lessonObject, $"{prefix}第 {index + 1} 节课程", errors, [], ["IsEnabled", "IsChangedClass"]);
                        CheckReference(lessonObject, "SubjectId", subjects, $"{prefix}第 {index + 1} 节科目", errors, true);
                    }
                    else errors.Add($"{prefix}第 {index + 1} 节课程必须是对象");
                }
                if (layoutId is not null && FindByGuid(layouts, layoutId) is JsonObject layout &&
                    Get(layout, "Layouts") is JsonArray points &&
                    classes.Count != points.OfType<JsonObject>().Count(point => Integer(point, "TimeType", 0) == 0))
                    errors.Add($"{prefix}的课程数量与关联时间表的上课时段数量不一致");
            }
            if (Get(plan, "TimeRule") is JsonObject rule)
            {
                var day = Integer(rule, "WeekDay", 0);
                var week = Integer(rule, "WeekCountDiv", 0);
                var total = Integer(rule, "WeekCountDivTotal", 2);
                CheckKnownFields(rule, ["WeekDay", "WeekCountDiv", "WeekCountDivTotal"], errors);
                if (day is < 0 or > 6) errors.Add($"{prefix}星期必须是 0 到 6");
                if (total < 1 || week < 0 || week > total)
                    errors.Add($"{prefix}多周轮换设置无效");
            }
            else if (Has(plan, "TimeRule")) errors.Add($"{prefix}触发规则必须是对象");
            CheckKnownFields(plan, ["TimeLayoutId", "TimeRule", "Classes", "AssociatedGroup", "OverlaySourceId"], errors);
            CheckReference(plan, "OverlaySourceId", plans, prefix + "临时层源课表", errors, true);
        }
        CheckReference(profile, "OverlayClassPlanId", plans, "档案临时层课表", errors, true);
        CheckReference(profile, "TempClassPlanId", plans, "档案临时课表", errors, true);
        if (Get(profile, "OrderedSchedules") is JsonObject ordered)
        {
            var dates = new HashSet<DateOnly>();
            foreach (var (date, value) in ordered)
            {
                if (value is JsonObject schedule)
                    CheckReference(schedule, "ClassPlanId", plans, $"预定课表 {date}", errors, false);
                else errors.Add($"预定课表 {date} 必须是对象");
                // 宿主以 DateTime 为键，同一天的不同写法（如带时区后缀）会在加载时冲突。
                if (TryDateKey(date, out var day) && !dates.Add(day)) errors.Add($"{day:yyyy-MM-dd} 安排了多个临时层或预定课表");
            }
        }
        else if (Has(profile, "OrderedSchedules")) errors.Add("OrderedSchedules 必须是对象");
        CheckOverlayCycles(layouts, "时间表", errors);
        CheckOverlayCycles(plans, "课表", errors);
        return errors;
    }

    /// <summary>
    /// 选择指定常规对象并补齐时间表、科目和临时层源课表依赖。null 表示该类别全部常规对象；
    /// 临时层只能经 <see cref="BuildTempLayerSelection"/> 按日期下发，不属于任何常规类别。
    /// </summary>
    public static string BuildSelection(string json, ProfileDistributionSection sections,
        IEnumerable<Guid>? timeLayoutIds = null, IEnumerable<Guid>? classPlanIds = null,
        IEnumerable<Guid>? subjectIds = null)
    {
        ValidateSections(sections);
        var profile = Parse(json);
        ThrowIfInvalid(profile);
        var layouts = Dictionary(profile, "TimeLayouts");
        var plans = Dictionary(profile, "ClassPlans");
        var subjects = Dictionary(profile, "Subjects");
        var selectedLayouts = SelectIds(layouts, sections.HasFlag(ProfileDistributionSection.TimeLayouts), timeLayoutIds, regularOnly: true);
        var selectedPlans = SelectIds(plans, sections.HasFlag(ProfileDistributionSection.ClassPlans), classPlanIds, regularOnly: true);
        var selectedSubjects = SelectIds(subjects, sections.HasFlag(ProfileDistributionSection.Subjects), subjectIds);
        return Serialize(SelectWithDependencies(profile, selectedLayouts, selectedPlans, selectedSubjects));
    }

    /// <summary>
    /// 只保留所选临时层课表（null 表示全部按日期安排的临时层）、它们的日期条目及依赖。
    /// 载荷中的来源课表仅用于设备核对临时层的来源，设备不会因此改动常规课表。
    /// </summary>
    public static string BuildTempLayerSelection(string json, IEnumerable<Guid>? tempLayerIds = null)
    {
        var profile = Parse(json);
        ThrowIfInvalid(profile);
        var scheduled = TempLayerSchedule(profile).Select(item => item.PlanId).ToHashSet();
        var selected = tempLayerIds?.ToHashSet() ?? scheduled;
        foreach (var id in selected)
            if (!scheduled.Contains(id)) throw new ArgumentException($"所选临时层“{id}”不存在或没有安排日期");
        if (selected.Count == 0) throw new ArgumentException("档案中没有可下发的临时层");
        var result = SelectWithDependencies(profile, [], [.. selected], []);
        // 指向常规课表的预定课表不是临时层，载荷只携带所选临时层的日期。
        if (Get(result, "OrderedSchedules") is JsonObject ordered)
            foreach (var key in ordered.Where(entry => entry.Value is not JsonObject schedule ||
                         ReferenceGuid(schedule, "ClassPlanId") is not { } planId || !selected.Contains(planId)).Select(entry => entry.Key).ToList())
                ordered.Remove(key);
        return Serialize(result);
    }

    private static JsonObject SelectWithDependencies(JsonObject profile, HashSet<Guid> selectedLayouts,
        HashSet<Guid> selectedPlans, HashSet<Guid> selectedSubjects)
    {
        var layouts = Dictionary(profile, "TimeLayouts");
        var plans = Dictionary(profile, "ClassPlans");
        var subjects = Dictionary(profile, "Subjects");
        // 临时层引用也属于依赖，循环引用不会导致无限循环。
        var queue = new Queue<Guid>(selectedPlans);
        while (queue.TryDequeue(out var id))
        {
            var plan = (JsonObject)FindByGuid(plans, id.ToString())!;
            if (ReferenceGuid(plan, "TimeLayoutId") is { } layoutId) selectedLayouts.Add(layoutId);
            if (Get(plan, "Classes") is JsonArray classes)
                foreach (var lesson in classes.OfType<JsonObject>())
                    if (ReferenceGuid(lesson, "SubjectId") is { } subjectId) selectedSubjects.Add(subjectId);
            if (ReferenceGuid(plan, "OverlaySourceId") is { } sourceId && selectedPlans.Add(sourceId)) queue.Enqueue(sourceId);
        }
        var layoutQueue = new Queue<Guid>(selectedLayouts);
        while (layoutQueue.TryDequeue(out var id))
            if (FindByGuid(layouts, id.ToString()) is JsonObject layout && Get(layout, "Layouts") is JsonArray points)
            {
                foreach (var point in points.OfType<JsonObject>())
                    if (ReferenceGuid(point, "DefaultClassId") is { } subjectId) selectedSubjects.Add(subjectId);
                if (ReferenceGuid(layout, "OverlaySourceId") is { } sourceId && selectedLayouts.Add(sourceId)) layoutQueue.Enqueue(sourceId);
            }
        Set(profile, "TimeLayouts", Filter(layouts, selectedLayouts));
        Set(profile, "ClassPlans", Filter(plans, selectedPlans));
        Set(profile, "Subjects", Filter(subjects, selectedSubjects));
        DropDanglingPlanReferences(profile);
        ThrowIfInvalid(profile);
        return profile;
    }

    /// <summary>只构造完整候选，不改变当前档案；替换仅清空明确选中的类别，依赖类别合并。</summary>
    public static string Apply(string currentJson, ProfileApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentException("请明确选择档案应用方式");
        if (request.Mode == ProfileApplyMode.TempLayers) return ApplyTempLayers(currentJson, request, DateTime.Today).Json;
        ValidateSections(request.Sections);
        var source = Parse(BuildSelection(request.ProfileJson, request.Sections));
        if (request.Mode == ProfileApplyMode.CreateAndActivate) return Serialize(source);
        var target = Parse(currentJson);
        foreach (var (field, section) in new[] { ("TimeLayouts", ProfileDistributionSection.TimeLayouts),
                     ("ClassPlans", ProfileDistributionSection.ClassPlans), ("Subjects", ProfileDistributionSection.Subjects) })
        {
            var targetDictionary = Dictionary(target, field);
            // 临时层不属于常规类别：整体替换只清空常规对象，设备上的临时层留待下面按依赖核对。
            if (request.Mode == ProfileApplyMode.ReplaceSections && request.Sections.HasFlag(section))
                foreach (var key in targetDictionary.Where(entry => field == "Subjects" || entry.Value is not JsonObject item || !IsOverlay(item))
                             .Select(entry => entry.Key).ToList())
                    targetDictionary.Remove(key);
            foreach (var (id, value) in Dictionary(source, field))
            {
                // GUID 字符串大小写不影响对象标识，避免同一 GUID 出现两个条目。
                var existingKey = targetDictionary.Select(entry => entry.Key).FirstOrDefault(key =>
                    Guid.TryParse(key, out var existing) && existing == Guid.Parse(id));
                targetDictionary[existingKey ?? id] = value?.DeepClone();
            }
            Set(target, field, targetDictionary);
        }
        // 课表群属于课表依赖，保持宿主组织结构及附加字段。
        if (Get(source, "ClassPlanGroups") is JsonObject sourceGroups && request.Sections.HasFlag(ProfileDistributionSection.ClassPlans))
        {
            var groups = Dictionary(target, "ClassPlanGroups");
            foreach (var (id, value) in sourceGroups) groups[id] = value?.DeepClone();
            Set(target, "ClassPlanGroups", groups);
        }
        // 替换或合并后按宿主规则修复设备上的临时层；宿主清理过期临时课表后也可能留下悬空指针。
        // 这些引用只指向已不存在的临时安排，清除后才能通过校验，否则设备档案将再也无法下发。
        RepairOverlays(target);
        DropDanglingPlanReferences(target);
        ThrowIfInvalid(target);
        return Serialize(target);
    }

    public sealed record TempLayerApplyResult(string Json, int Applied, int Skipped);

    /// <summary>
    /// 把载荷中按日期安排的临时层写入当前档案的候选副本。早于 <paramref name="today"/> 的跳过；
    /// 设备同日已有安排时，只有请求明确允许才替换。科目、课表群只补缺，设备常规时间表与课表不被改动。
    /// </summary>
    public static TempLayerApplyResult ApplyTempLayers(string currentJson, ProfileApplyRequest request, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = Parse(request.ProfileJson);
        ThrowIfInvalid(source);
        var schedule = TempLayerSchedule(source);
        if (schedule.Count == 0) throw new ArgumentException("载荷中没有按日期安排的临时层");
        var pending = schedule.Where(item => item.Date >= DateOnly.FromDateTime(today)).ToList();
        if (pending.Count == 0) throw new ArgumentException("所选临时层的日期都已过去，没有可下发的内容");
        if (pending.GroupBy(item => item.Date).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new ArgumentException($"{duplicate.Key:yyyy-MM-dd} 安排了多个临时层，每天只能有一个");

        var target = Parse(currentJson);
        // 先清除宿主清理后残留的悬空日期条目，它们不算设备上已有的安排。
        DropDanglingPlanReferences(target);
        var ordered = Dictionary(target, "OrderedSchedules");
        var conflicts = pending.Where(item => DateKeys(ordered, item.Date).Count > 0).Select(item => item.Date.ToString("yyyy-MM-dd")).ToList();
        if (conflicts.Count > 0 && !request.ReplaceExistingTempLayers)
            throw new ArgumentException($"设备在 {string.Join("、", conflicts)} 已有临时层或预定课表；如需覆盖，请勾选替换后重试");

        var sourcePlans = Dictionary(source, "ClassPlans");
        var sourceLayouts = Dictionary(source, "TimeLayouts");
        var sourceSubjects = Dictionary(source, "Subjects");
        var sourceGroups = Dictionary(source, "ClassPlanGroups");
        var plans = Dictionary(target, "ClassPlans");
        var layouts = Dictionary(target, "TimeLayouts");
        var subjects = Dictionary(target, "Subjects");
        var groups = Dictionary(target, "ClassPlanGroups");
        foreach (var (date, sourcePlanId) in pending)
        {
            foreach (var key in DateKeys(ordered, date))
            {
                var replaced = ordered[key] is JsonObject old ? ReferenceGuid(old, "ClassPlanId") : null;
                ordered.Remove(key);
                // 被替换的临时层不再被任何日期引用时一并删除，与宿主清理规则一致；常规课表保留。
                if (replaced is { } replacedId && FindByGuid(plans, replacedId.ToString()) is JsonObject replacedPlan &&
                    IsOverlay(replacedPlan) && !ordered.Any(entry => entry.Value is JsonObject other && ReferenceGuid(other, "ClassPlanId") == replacedId))
                    plans.Remove(KeyOf(plans, replacedId)!);
            }

            var plan = FindByGuid(sourcePlans, sourcePlanId.ToString())!.DeepClone().AsObject();
            var sourceLayout = ReferenceGuid(plan, "TimeLayoutId") is { } sourceLayoutId ? FindByGuid(sourceLayouts, sourceLayoutId.ToString()) as JsonObject : null;
            if (sourceLayout is null) throw new ArgumentException($"{date:yyyy-MM-dd} 的临时层缺少时间表");
            var neededSubjects = new HashSet<Guid>();
            foreach (var lesson in (Get(plan, "Classes") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (ReferenceGuid(lesson, "SubjectId") is { } subjectId) neededSubjects.Add(subjectId);
            foreach (var point in Points(sourceLayout))
                if (ReferenceGuid(point, "DefaultClassId") is { } subjectId) neededSubjects.Add(subjectId);
            foreach (var subjectId in neededSubjects)
                if (FindByGuid(subjects, subjectId.ToString()) is null && FindByGuid(sourceSubjects, subjectId.ToString()) is { } subject)
                    subjects[subjectId.ToString()] = subject.DeepClone();

            if (ReferenceGuid(plan, "AssociatedGroup") is { } groupId && groupId != DefaultClassPlanGroupId &&
                FindByGuid(groups, groupId.ToString()) is null)
            {
                if (FindByGuid(sourceGroups, groupId.ToString()) is { } group) groups[groupId.ToString()] = group.DeepClone();
                else Set(plan, "AssociatedGroup", DefaultClassPlanGroupId.ToString());
            }

            var layoutId = ReferenceGuid(plan, "TimeLayoutId")!.Value;
            if (FindByGuid(layouts, layoutId.ToString()) is not JsonObject deviceLayout || !SameTimePoints(deviceLayout, sourceLayout))
            {
                // 设备没有这张时间表或时段不同：写成临时层时间表副本，不改动设备的常规时间表。
                var copy = sourceLayout.DeepClone().AsObject();
                var layoutOrigin = IsOverlay(sourceLayout) ? ReferenceGuid(sourceLayout, "OverlaySourceId") : layoutId;
                Set(copy, "IsOverlay", true);
                Set(copy, "OverlaySourceId", layoutOrigin is { } originId && FindByGuid(layouts, originId.ToString()) is not null ? originId.ToString() : null);
                if (!IsOverlay(sourceLayout) && Text(copy, "Name") is { } layoutName && !layoutName.EndsWith("（临时层）", StringComparison.Ordinal))
                    Set(copy, "Name", layoutName + "（临时层）");
                layoutId = Guid.NewGuid();
                layouts[layoutId.ToString()] = copy;
                Set(plan, "TimeLayoutId", layoutId.ToString());
            }

            var dateText = date.ToString("yyyy-MM-dd") + "T00:00:00";
            Set(plan, "IsOverlay", true);
            Set(plan, "OverlaySetupTime", dateText);
            var originPlan = ReferenceGuid(plan, "OverlaySourceId") is { } originPlanId ? FindByGuid(plans, originPlanId.ToString()) as JsonObject : null;
            if (originPlan is null) Set(plan, "OverlaySourceId", null);
            MarkChangedClasses(plan, originPlan);
            var planId = Guid.NewGuid();
            plans[planId.ToString()] = plan;
            ordered[dateText] = new JsonObject { ["ClassPlanId"] = planId.ToString() };
            if (date == DateOnly.FromDateTime(today))
            {
                Set(target, "OverlayClassPlanId", planId.ToString());
                Set(target, "IsOverlayClassPlanEnabled", true);
            }
        }
        Set(target, "Subjects", subjects);
        Set(target, "ClassPlanGroups", groups);
        Set(target, "TimeLayouts", layouts);
        Set(target, "ClassPlans", plans);
        Set(target, "OrderedSchedules", ordered);
        RemoveUnusedOverlayLayouts(target);
        DropDanglingPlanReferences(target);
        ThrowIfInvalid(target);
        return new TempLayerApplyResult(Serialize(target), pending.Count, schedule.Count - pending.Count);
    }

    /// <summary>
    /// 规范化从设备收集的档案：按宿主 RefreshClassesList 的规则对齐课程数与上课时段数，
    /// 并清除宿主清理过期临时层后残留的悬空指针，使收集结果可以直接编辑保存。
    /// </summary>
    public static string NormalizeCollected(string json)
    {
        var profile = Parse(json);
        var layouts = Dictionary(profile, "TimeLayouts");
        foreach (var plan in Dictionary(profile, "ClassPlans").Select(entry => entry.Value).OfType<JsonObject>())
            if (Get(plan, "Classes") is JsonArray) AlignClasses(plan, layouts);
        DropDanglingPlanReferences(profile);
        return Serialize(profile);
    }

    /// <summary>日期条目指向的临时层课表（IsOverlay），按日期排序。</summary>
    private static List<(DateOnly Date, Guid PlanId)> TempLayerSchedule(JsonObject profile)
    {
        var plans = Dictionary(profile, "ClassPlans");
        var result = new List<(DateOnly, Guid)>();
        if (Get(profile, "OrderedSchedules") is not JsonObject ordered) return result;
        foreach (var (key, value) in ordered)
            if (TryDateKey(key, out var date) && value is JsonObject schedule && ReferenceGuid(schedule, "ClassPlanId") is { } planId &&
                FindByGuid(plans, planId.ToString()) is JsonObject plan && IsOverlay(plan))
                result.Add((date, planId));
        return result.OrderBy(item => item.Item1).ToList();
    }

    /// <summary>OrderedSchedules 的键是 DateTime；宿主可能带时区后缀，只取日期部分比较。</summary>
    private static bool TryDateKey(string key, out DateOnly date)
    {
        date = default;
        return key.Length >= 10 && DateOnly.TryParseExact(key[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static List<string> DateKeys(JsonObject ordered, DateOnly date) =>
        ordered.Where(entry => TryDateKey(entry.Key, out var value) && value == date).Select(entry => entry.Key).ToList();

    private static bool IsOverlay(JsonObject node) =>
        Get(node, "IsOverlay") is JsonValue value && value.TryGetValue<bool>(out var overlay) && overlay;

    /// <summary>只比较会影响课程对应关系的时段类型与起止时间，不受时间字段新旧格式影响。</summary>
    private static bool SameTimePoints(JsonObject left, JsonObject right)
    {
        var a = Points(left).ToList();
        var b = Points(right).ToList();
        return a.Count == b.Count && a.Zip(b).All(pair =>
            Integer(pair.First, "TimeType", 0) == Integer(pair.Second, "TimeType", 0) &&
            TryReadTime(pair.First, true, out var startA) && TryReadTime(pair.Second, true, out var startB) && startA == startB &&
            TryReadTime(pair.First, false, out var endA) && TryReadTime(pair.Second, false, out var endB) && endA == endB);
    }

    /// <summary>与宿主 RefreshIsChangedClass 一致：来源课表缺失或节数不同则全部视为未换课。</summary>
    private static void MarkChangedClasses(JsonObject plan, JsonObject? origin)
    {
        if (Get(plan, "Classes") is not JsonArray classes) return;
        var originClasses = origin is null ? null : Get(origin, "Classes") as JsonArray;
        for (var index = 0; index < classes.Count; index++)
        {
            if (classes[index] is not JsonObject lesson) continue;
            var changed = originClasses is not null && originClasses.Count == classes.Count &&
                          ReferenceGuid(lesson, "SubjectId") != (originClasses[index] is JsonObject before ? ReferenceGuid(before, "SubjectId") : null);
            Set(lesson, "IsChangedClass", changed);
        }
    }

    /// <summary>
    /// 按宿主规则修复依赖变化的临时层，而不是直接丢弃老师的换课：课程数随时间表补齐或截断，
    /// 缺失的科目改为空课，缺失的课表群改为默认课表群，来源对象缺失只清空来源指针。
    /// 只有所用时间表已不存在时，临时层才无法显示而被移除。
    /// </summary>
    private static void RepairOverlays(JsonObject profile)
    {
        var layouts = Dictionary(profile, "TimeLayouts");
        var plans = Dictionary(profile, "ClassPlans");
        var subjects = Dictionary(profile, "Subjects");
        var groups = Dictionary(profile, "ClassPlanGroups");
        void ClearMissingSubject(JsonObject node, string field)
        {
            if (ReferenceGuid(node, field) is { } id && FindByGuid(subjects, id.ToString()) is null) Set(node, field, Guid.Empty.ToString());
        }
        foreach (var layout in layouts.Select(entry => entry.Value).OfType<JsonObject>().Where(IsOverlay))
        {
            foreach (var point in Points(layout)) ClearMissingSubject(point, "DefaultClassId");
            if (ReferenceGuid(layout, "OverlaySourceId") is { } source && FindByGuid(layouts, source.ToString()) is null)
                Set(layout, "OverlaySourceId", null);
        }
        foreach (var key in plans.Where(entry => entry.Value is JsonObject plan && IsOverlay(plan)).Select(entry => entry.Key).ToList())
        {
            var plan = (JsonObject)plans[key]!;
            if (!AlignClasses(plan, layouts))
            {
                plans.Remove(key);
                continue;
            }
            foreach (var lesson in (Get(plan, "Classes") as JsonArray)!.OfType<JsonObject>()) ClearMissingSubject(lesson, "SubjectId");
            if (ReferenceGuid(plan, "AssociatedGroup") is { } groupId && groupId != DefaultClassPlanGroupId && FindByGuid(groups, groupId.ToString()) is null)
                Set(plan, "AssociatedGroup", DefaultClassPlanGroupId.ToString());
            if (ReferenceGuid(plan, "OverlaySourceId") is { } source && FindByGuid(plans, source.ToString()) is null)
                Set(plan, "OverlaySourceId", null);
            MarkChangedClasses(plan, ReferenceGuid(plan, "OverlaySourceId") is { } origin ? FindByGuid(plans, origin.ToString()) as JsonObject : null);
        }
        RemoveUnusedOverlayLayouts(profile);
    }

    /// <summary>与宿主 RefreshClassesList 一致：课程数补齐或截断为关联时间表的上课时段数。时间表不存在时返回 false。</summary>
    private static bool AlignClasses(JsonObject plan, JsonObject layouts)
    {
        if (ReferenceGuid(plan, "TimeLayoutId") is not { } layoutId || FindByGuid(layouts, layoutId.ToString()) is not JsonObject layout) return false;
        if (Get(plan, "Classes") is not JsonArray classes) Set(plan, "Classes", classes = new JsonArray());
        var lessons = Points(layout).Count(point => Integer(point, "TimeType", 0) == 0);
        while (classes.Count > lessons) classes.RemoveAt(classes.Count - 1);
        while (classes.Count < lessons) classes.Add(new JsonObject { ["SubjectId"] = Guid.Empty.ToString() });
        return true;
    }

    private static IEnumerable<JsonObject> Points(JsonObject layout) => (Get(layout, "Layouts") as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    /// <summary>与宿主清理规则一致：没有任何课表使用的临时层时间表随之删除。</summary>
    private static void RemoveUnusedOverlayLayouts(JsonObject profile)
    {
        var layouts = Dictionary(profile, "TimeLayouts");
        var used = Dictionary(profile, "ClassPlans").Select(entry => entry.Value as JsonObject)
            .Select(plan => plan is null ? null : ReferenceGuid(plan, "TimeLayoutId")).OfType<Guid>().ToHashSet();
        foreach (var key in layouts.Where(entry => entry.Value is JsonObject layout && IsOverlay(layout) &&
                     Guid.TryParse(entry.Key, out var id) && !used.Contains(id)).Select(entry => entry.Key).ToList())
            layouts.Remove(key);
    }

    private static string? KeyOf(JsonObject dictionary, Guid id) =>
        dictionary.Select(entry => entry.Key).FirstOrDefault(key => Guid.TryParse(key, out var value) && value == id);

    /// <summary>
    /// 清除指向不存在课表的预定课表与临时课表指针。指针置为空 GUID 而不是 null，
    /// 以便宿主无论把字段声明为 Guid 还是 Guid? 都能反序列化。
    /// </summary>
    private static void DropDanglingPlanReferences(JsonObject profile)
    {
        var plans = Dictionary(profile, "ClassPlans");
        bool Missing(Guid id) => FindByGuid(plans, id.ToString()) is null;
        foreach (var field in new[] { "OverlayClassPlanId", "TempClassPlanId" })
            if (ReferenceGuid(profile, field) is { } id && Missing(id)) Set(profile, field, Guid.Empty.ToString());
        if (Get(profile, "OrderedSchedules") is JsonObject ordered)
            foreach (var key in ordered.Where(entry => entry.Value is JsonObject schedule &&
                         ReferenceGuid(schedule, "ClassPlanId") is { } id && Missing(id)).Select(entry => entry.Key).ToList())
                ordered.Remove(key);
    }

    private static void ValidateSections(ProfileDistributionSection sections)
    {
        if (sections == ProfileDistributionSection.None || (sections & ~AllSections) != 0)
            throw new ArgumentException("请至少选择一种有效的档案类别");
    }

    private static void ThrowIfInvalid(JsonObject profile)
    {
        var errors = Validate(profile);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
    }

    private static JsonObject ReadDictionary(JsonObject profile, string name, List<string> errors, bool allowEmptyIds = false)
    {
        var value = Get(profile, name);
        if (value is null && !Has(profile, name)) return new JsonObject();
        if (value is not JsonObject dictionary) { errors.Add($"{name} 必须是以 GUID 为键的对象"); return new JsonObject(); }
        var seen = new HashSet<Guid>();
        foreach (var (id, node) in dictionary)
        {
            if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty && !allowEmptyIds) errors.Add($"{name} 对象 ID“{id}”不是有效 GUID");
            else if (!seen.Add(guid)) errors.Add($"{name} 存在重复对象 ID“{id}”");
            if (node is not JsonObject) errors.Add($"{name} 对象“{id}”必须是对象");
        }
        return dictionary;
    }

    private static void CheckTypes(JsonObject node, string description, List<string> errors,
        IReadOnlyList<string> strings, IReadOnlyList<string> booleans)
    {
        foreach (var field in strings)
            if (Has(node, field) && (Get(node, field) is not JsonValue primitive || !primitive.TryGetValue<string>(out _)))
                errors.Add($"{description}的 {field} 必须是字符串");
        foreach (var field in booleans)
            if (Has(node, field) && (Get(node, field) is not JsonValue primitive || !primitive.TryGetValue<bool>(out _)))
                errors.Add($"{description}的 {field} 必须是布尔值");
        CheckKnownFields(node, [.. strings, .. booleans], errors);
    }

    private static void CheckGuidValue(JsonObject node, string field, string description, List<string> errors)
    {
        if (Has(node, field) && !Guid.TryParse(Text(node, field), out _))
            errors.Add($"{description}的 {field} 必须是 GUID 字符串");
    }

    private static void CheckKnownFields(JsonObject node, IReadOnlyList<string> fields, List<string> errors)
    {
        foreach (var field in fields)
            if (node.Count(entry => entry.Key.Equals(field, StringComparison.OrdinalIgnoreCase)) > 1)
                errors.Add($"字段 {field} 存在大小写不同的重复定义");
    }

    private static void Materialize(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var (_, child) in obj)
                if (child is not null) Materialize(child);
        if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) Materialize(child);
    }

    private static void CheckOverlayCycles(JsonObject dictionary, string description, List<string> errors)
    {
        var edges = new Dictionary<Guid, Guid>();
        foreach (var (id, value) in dictionary)
            if (Guid.TryParse(id, out var parsedId) && value is JsonObject node && ReferenceGuid(node, "OverlaySourceId") is { } source)
                edges[parsedId] = source;
        var completed = new HashSet<Guid>();
        foreach (var start in edges.Keys)
        {
            if (completed.Contains(start)) continue;
            var chain = new HashSet<Guid>();
            var current = start;
            while (!completed.Contains(current) && edges.TryGetValue(current, out var next))
            {
                if (!chain.Add(current))
                {
                    errors.Add($"{description}临时层源引用存在循环“{current}”");
                    break;
                }
                current = next;
            }
            completed.UnionWith(chain);
        }
    }

    private static string? CheckReference(JsonObject node, string field, JsonObject dictionary,
        string description, List<string> errors, bool allowEmpty)
    {
        CheckKnownFields(node, [field], errors);
        var value = Get(node, field);
        if (value is null && allowEmpty) return null;
        var text = Text(node, field);
        if (allowEmpty && text == string.Empty) return null;
        if (!Guid.TryParse(text, out var id)) { errors.Add($"{description} ID 无效"); return null; }
        if (id == Guid.Empty && allowEmpty) return null;
        if (FindByGuid(dictionary, text!) is null) { errors.Add($"{description}引用了不存在的对象“{text}”"); return null; }
        return text;
    }

    private static HashSet<Guid> SelectIds(JsonObject dictionary, bool selected, IEnumerable<Guid>? ids, bool regularOnly = false)
    {
        if (!selected) return [];
        var result = (ids ?? dictionary.Where(entry => !regularOnly || entry.Value is not JsonObject item || !IsOverlay(item))
            .Select(entry => Guid.Parse(entry.Key))).ToHashSet();
        foreach (var id in result)
        {
            if (FindByGuid(dictionary, id.ToString()) is not JsonObject item) throw new ArgumentException($"选中的档案对象“{id}”不存在");
            if (regularOnly && IsOverlay(item)) throw new ArgumentException("临时层不属于常规类别，请改用“作为临时层下发”");
        }
        return result;
    }

    private static JsonObject Filter(JsonObject dictionary, HashSet<Guid> ids)
    {
        var filtered = new JsonObject();
        foreach (var (id, value) in dictionary)
            if (ids.Contains(Guid.Parse(id))) filtered[id] = value?.DeepClone();
        return filtered;
    }

    private static JsonNode? FindByGuid(JsonObject dictionary, string id) =>
        Guid.TryParse(id, out var guid) && KeyOf(dictionary, guid) is { } key ? dictionary[key] : null;
    private static Guid? ReferenceGuid(JsonObject node, string field) => Guid.TryParse(Text(node, field), out var id) && id != Guid.Empty ? id : null;
    private static JsonNode? Get(JsonObject node, string field) => node.FirstOrDefault(entry => entry.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value;
    private static bool Has(JsonObject node, string field) => node.Any(entry => entry.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
    private static string? Text(JsonObject node, string field) => Get(node, field) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Integer(JsonObject node, string field, int fallback) => Get(node, field) is null ? fallback :
        Get(node, field) is JsonValue value && value.TryGetValue<int>(out var number) ? number : int.MinValue;
    private static JsonObject Dictionary(JsonObject node, string field) => Get(node, field) as JsonObject ?? new JsonObject();
    private static void Set(JsonObject node, string field, JsonNode? value)
    {
        var key = node.Select(entry => entry.Key).FirstOrDefault(key => key.Equals(field, StringComparison.OrdinalIgnoreCase)) ?? field;
        if (ReferenceEquals(node[key], value)) return;
        node[key] = value;
    }

    /// <summary>读取档案时段的钟面时间，兼容正式 TimeSpan、旧秒数以及旧 DateTime；不转换时区。</summary>
    public static bool TryReadTime(JsonObject point, bool start, out TimeSpan time) =>
        TryTime(point, start ? "StartTime" : "EndTime", start ? "StartSecond" : "EndSecond", out time);

    private static bool TryTime(JsonObject point, string field, string legacyField, out TimeSpan time)
    {
        time = default;
        if (Get(point, field) is not null)
            return TimeSpan.TryParse(Text(point, field), CultureInfo.InvariantCulture, out time);
        var legacy = Get(point, legacyField);
        var text = legacy is JsonValue value && value.TryGetValue<string>(out var stringValue) ? stringValue : legacy?.ToJsonString();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (!double.IsFinite(seconds) || seconds is < 0 or >= 86400) return false;
            time = TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (text?.Contains('T') == true && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateTime))
        {
            time = dateTime.TimeOfDay;
            return true;
        }
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }
}
