using System.Globalization;
using System.Text.Json.Nodes;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 临时层（IsOverlay 课表及其专用时间表，由 OrderedSchedules[日期] 指向）与设备收集结果的处理。
/// 规则对照 ClassIsland 宿主的 CreateTempClassPlan、CleanExpiredTempClassPlan 与 RefreshClassesList。
/// </summary>
public static partial class ProfileDocument
{
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
            // 宿主每次加载课表都会按今天的日期条目重算当前临时层指针，这里只为立即生效预先设置。
            if (date == DateOnly.FromDateTime(today)) Set(target, "OverlayClassPlanId", planId.ToString());
        }
        // 宿主 GetClassPlanByDate 只有在此开关打开时才按日期使用临时层（不论哪天），与宿主 CreateTempClassPlan 一致总是打开。
        Set(target, "IsOverlayClassPlanEnabled", true);
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
}
