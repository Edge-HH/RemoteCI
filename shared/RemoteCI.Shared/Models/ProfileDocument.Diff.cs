using System.Globalization;
using System.Text.Json.Nodes;

namespace RemoteCI.Shared.Models;

/// <summary>档案的一处可读差异，如“课表「周三」第 4 节 数学 → 物理”。</summary>
public sealed record ProfileChange(string Category, string Text);

public static partial class ProfileDocument
{
    public const int MaxDiffChanges = 200;
    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>
    /// 比较两份档案的科目、时间表、课表与临时层，只报告影响上课安排的变化。对象先按 ID 配对，
    /// ID 对不上时按名称配对（设备档案可能来自别处导入，ID 与服务端不同）。
    /// </summary>
    public static IReadOnlyList<ProfileChange> Diff(string beforeJson, string afterJson, bool includeTempLayers = true)
    {
        var before = Parse(beforeJson);
        var after = Parse(afterJson);
        var changes = new List<ProfileChange>();
        var subjectsBefore = Entries(before, "Subjects");
        var subjectsAfter = Entries(after, "Subjects");
        DiffSubjects(subjectsBefore, subjectsAfter, changes);
        DiffLayouts(Entries(before, "TimeLayouts", overlay: false), Entries(after, "TimeLayouts", overlay: false), changes);
        var layoutsBefore = Entries(before, "TimeLayouts");
        var layoutsAfter = Entries(after, "TimeLayouts");
        var plansBefore = Entries(before, "ClassPlans");
        var plansAfter = Entries(after, "ClassPlans");
        foreach (var (left, right) in Pair(
                     plansBefore.Where(x => !IsOverlay(x.Value)).ToDictionary(),
                     plansAfter.Where(x => !IsOverlay(x.Value)).ToDictionary(), changes, "课表"))
            DiffPlan(PlanLabel(right), left, right, subjectsBefore, subjectsAfter, layoutsBefore, layoutsAfter, changes);
        if (includeTempLayers) DiffTempLayers(before, after, plansBefore, plansAfter, subjectsBefore, subjectsAfter, changes);
        return changes.Count <= MaxDiffChanges
            ? changes
            : [.. changes.Take(MaxDiffChanges), new ProfileChange("其他", $"另有 {changes.Count - MaxDiffChanges} 处差异未列出")];
    }

    private static Dictionary<Guid, JsonObject> Entries(JsonObject profile, string section, bool? overlay = null)
    {
        var result = new Dictionary<Guid, JsonObject>();
        foreach (var (key, node) in Dictionary(profile, section))
        {
            if (!Guid.TryParse(key, out var id) || node is not JsonObject item) continue;
            if (overlay is { } wanted && IsOverlay(item) != wanted) continue;
            result[id] = item;
        }
        return result;
    }

    /// <summary>按 ID、再按名称配对；未配对的记为新增或删除，返回配对成功的 (旧, 新)。</summary>
    private static List<(JsonObject Before, JsonObject After)> Pair(
        Dictionary<Guid, JsonObject> before, Dictionary<Guid, JsonObject> after, List<ProfileChange> changes, string category)
    {
        var pairs = new List<(JsonObject, JsonObject)>();
        var unmatchedAfter = new Dictionary<Guid, JsonObject>(after);
        var unmatchedBefore = new List<JsonObject>();
        foreach (var (id, item) in before)
        {
            if (unmatchedAfter.Remove(id, out var match)) pairs.Add((item, match));
            else unmatchedBefore.Add(item);
        }
        foreach (var item in unmatchedBefore)
        {
            var name = Text(item, "Name");
            var match = unmatchedAfter.FirstOrDefault(x => name is not null && Text(x.Value, "Name") == name);
            if (match.Value is not null)
            {
                unmatchedAfter.Remove(match.Key);
                pairs.Add((item, match.Value));
            }
            else changes.Add(new ProfileChange(category, $"删除{category}「{NameOf(item, category)}」"));
        }
        foreach (var item in unmatchedAfter.Values)
            changes.Add(new ProfileChange(category, $"新增{category}「{NameOf(item, category)}」"));
        return pairs;
    }

    private static void DiffSubjects(Dictionary<Guid, JsonObject> before, Dictionary<Guid, JsonObject> after, List<ProfileChange> changes)
    {
        foreach (var (left, right) in Pair(before, after, changes, "科目"))
        {
            var oldName = Text(left, "Name") ?? "未命名";
            var newName = Text(right, "Name") ?? "未命名";
            if (oldName != newName) changes.Add(new ProfileChange("科目", $"科目「{oldName}」改名为「{newName}」"));
            var oldTeacher = Blank(Text(left, "TeacherName"));
            var newTeacher = Blank(Text(right, "TeacherName"));
            if (oldTeacher != newTeacher)
                changes.Add(new ProfileChange("科目", $"「{newName}」任课教师 {oldTeacher ?? "未设置"} → {newTeacher ?? "未设置"}"));
        }
    }

    private static void DiffLayouts(Dictionary<Guid, JsonObject> before, Dictionary<Guid, JsonObject> after, List<ProfileChange> changes)
    {
        foreach (var (left, right) in Pair(before, after, changes, "时间表"))
        {
            var name = Text(right, "Name") ?? "未命名";
            if (Text(left, "Name") is { } oldName && oldName != name)
                changes.Add(new ProfileChange("时间表", $"时间表「{oldName}」改名为「{name}」"));
            var oldPoints = Points(left).ToList();
            var newPoints = Points(right).ToList();
            if (oldPoints.Count != newPoints.Count)
                changes.Add(new ProfileChange("时间表", $"时间表「{name}」时段数 {oldPoints.Count} → {newPoints.Count}"));
            var lesson = 0;
            for (var i = 0; i < Math.Min(oldPoints.Count, newPoints.Count); i++)
            {
                var type = Integer(newPoints[i], "TimeType", 0);
                if (type == 0) lesson++;
                var oldText = PointText(oldPoints[i]);
                var newText = PointText(newPoints[i]);
                if (oldText != newText)
                    changes.Add(new ProfileChange("时间表",
                        $"时间表「{name}」{(type == 0 ? $"第 {lesson} 节" : $"第 {i + 1} 个时段")} {oldText} → {newText}"));
            }
        }
    }

    private static void DiffPlan(
        string label, JsonObject left, JsonObject right,
        Dictionary<Guid, JsonObject> subjectsBefore, Dictionary<Guid, JsonObject> subjectsAfter,
        Dictionary<Guid, JsonObject> layoutsBefore, Dictionary<Guid, JsonObject> layoutsAfter,
        List<ProfileChange> changes, string category = "课表")
    {
        if (Text(left, "Name") is { } oldName && Text(right, "Name") is { } newName && oldName != newName)
            changes.Add(new ProfileChange(category, $"{category}「{oldName}」改名为「{newName}」"));
        var oldDay = Get(left, "TimeRule") is JsonObject oldRule ? Integer(oldRule, "WeekDay", 0) : -1;
        var newDay = Get(right, "TimeRule") is JsonObject newRule ? Integer(newRule, "WeekDay", 0) : -1;
        if (oldDay != newDay && oldDay is >= 0 and <= 6 && newDay is >= 0 and <= 6)
            changes.Add(new ProfileChange(category, $"{label}的星期 {WeekdayNames[oldDay]} → {WeekdayNames[newDay]}"));
        var oldLayout = LayoutName(left, layoutsBefore);
        var newLayout = LayoutName(right, layoutsAfter);
        if (oldLayout != newLayout)
            changes.Add(new ProfileChange(category, $"{label}的时间表 {oldLayout ?? "未设置"} → {newLayout ?? "未设置"}"));
        var oldLessons = Get(left, "Classes") as JsonArray ?? [];
        var newLessons = Get(right, "Classes") as JsonArray ?? [];
        if (oldLessons.Count != newLessons.Count)
            changes.Add(new ProfileChange(category, $"{label}的课程数 {oldLessons.Count} → {newLessons.Count}"));
        for (var i = 0; i < Math.Min(oldLessons.Count, newLessons.Count); i++)
        {
            var oldSubject = LessonSubject(oldLessons[i], subjectsBefore);
            var newSubject = LessonSubject(newLessons[i], subjectsAfter);
            if (oldSubject != newSubject)
                changes.Add(new ProfileChange(category, $"{label}第 {i + 1} 节 {oldSubject} → {newSubject}"));
        }
    }

    private static void DiffTempLayers(
        JsonObject before, JsonObject after,
        Dictionary<Guid, JsonObject> plansBefore, Dictionary<Guid, JsonObject> plansAfter,
        Dictionary<Guid, JsonObject> subjectsBefore, Dictionary<Guid, JsonObject> subjectsAfter,
        List<ProfileChange> changes)
    {
        var oldDates = OverlayByDate(before, plansBefore);
        var newDates = OverlayByDate(after, plansAfter);
        var layoutsBefore = Entries(before, "TimeLayouts");
        var layoutsAfter = Entries(after, "TimeLayouts");
        foreach (var date in oldDates.Keys.Union(newDates.Keys).Order())
        {
            var label = $"{date:yyyy-MM-dd} 临时层";
            switch (oldDates.TryGetValue(date, out var left), newDates.TryGetValue(date, out var right))
            {
                case (true, false): changes.Add(new ProfileChange("临时层", $"删除 {date:yyyy-MM-dd} 的临时层")); break;
                case (false, true): changes.Add(new ProfileChange("临时层", $"新增 {date:yyyy-MM-dd} 的临时层「{Text(right!, "Name") ?? "未命名"}」")); break;
                case (true, true):
                    DiffPlan(label, left!, right!, subjectsBefore, subjectsAfter, layoutsBefore, layoutsAfter, changes, "临时层");
                    break;
            }
        }
    }

    private static Dictionary<DateOnly, JsonObject> OverlayByDate(JsonObject profile, Dictionary<Guid, JsonObject> plans)
    {
        var result = new Dictionary<DateOnly, JsonObject>();
        foreach (var (key, node) in Dictionary(profile, "OrderedSchedules"))
        {
            if (!TryDateKey(key, out var date) || node is not JsonObject schedule) continue;
            if (ReferenceGuid(schedule, "ClassPlanId") is { } planId && plans.TryGetValue(planId, out var plan) && IsOverlay(plan))
                result[date] = plan;
        }
        return result;
    }

    private static string PlanLabel(JsonObject plan)
    {
        var name = Text(plan, "Name");
        if (!string.IsNullOrWhiteSpace(name)) return $"课表「{name}」";
        var day = Get(plan, "TimeRule") is JsonObject rule ? Integer(rule, "WeekDay", -1) : -1;
        return day is >= 0 and <= 6 ? $"课表「{WeekdayNames[day]}」" : "课表";
    }

    private static string? LayoutName(JsonObject plan, Dictionary<Guid, JsonObject> layouts) =>
        ReferenceGuid(plan, "TimeLayoutId") is { } id && layouts.TryGetValue(id, out var layout) ? Text(layout, "Name") ?? id.ToString() : null;

    private static string LessonSubject(JsonNode? lesson, Dictionary<Guid, JsonObject> subjects) =>
        lesson is JsonObject item && ReferenceGuid(item, "SubjectId") is { } id
            ? subjects.TryGetValue(id, out var subject) ? Text(subject, "Name") ?? "未命名科目" : "未知科目"
            : "空课";

    private static string PointText(JsonObject point)
    {
        var type = Integer(point, "TimeType", 0) switch { 0 => "上课", 1 => "课间", 2 => "分割线", 3 => "行动", _ => "未知" };
        return TryReadTime(point, true, out var start) && TryReadTime(point, false, out var end)
            ? $"{type} {start.ToString(@"hh\:mm", CultureInfo.InvariantCulture)}–{end.ToString(@"hh\:mm", CultureInfo.InvariantCulture)}"
            : type;
    }

    private static string NameOf(JsonObject item, string category) => Text(item, "Name") is { Length: > 0 } name ? name : $"未命名{category}";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
