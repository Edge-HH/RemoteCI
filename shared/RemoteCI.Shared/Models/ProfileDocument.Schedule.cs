using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RemoteCI.Shared.Models;

/// <summary>按档案推算的课表及推算时无法确定的信息。</summary>
/// <param name="UsesWeekRotation">
/// 有日期需要在多周轮换课表之间选择，但管理员没有设置学期开始日期。轮换起始周保存在教室端 ClassIsland 设置里、不在档案中，
/// 此时推算按“本周为轮换第 1 周”处理，可能与教室端实际轮换不一致；设置学期开始日期后按教学周推算，不再标记。
/// </param>
public sealed record ProfileScheduleResult(ScheduleBundle Bundle, bool UsesWeekRotation);

/// <summary>
/// 不依赖插件，直接从服务端保存的档案推算未来几日课表：插件从未连接或尚未同步课表时，课表页据此展示当前档案。
/// 选课规则对照 ClassIsland 宿主的 GetClassPlanByDate：按日期安排的预定课表/临时层优先，其次按星期、课表群与轮换周匹配常规课表。
/// </summary>
public static partial class ProfileDocument
{
    /// <summary>档案中未设置科目的占位名，与插件 StateCollector.UnsetSubjectPlaceholder 一致。</summary>
    private const string UnsetSubjectPlaceholder = "???";

    public static ProfileScheduleResult BuildSchedule(
        string json, DateOnly from, int days = 7, Func<DateOnly, HolidayCalendarDay?>? holidays = null, DateOnly? semesterStart = null)
    {
        var profile = Parse(json);
        var subjects = Dictionary(profile, "Subjects");
        var usesRotation = false;
        var bundle = new ScheduleBundle
        {
            FromDate = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            GeneratedAt = DateTimeOffset.UtcNow,
            Source = ScheduleBundleSources.Profile,
            Subjects = subjects
                .Where(entry => Guid.TryParse(entry.Key, out _) && entry.Value is JsonObject)
                .Select(entry => (Id: Guid.Parse(entry.Key), Subject: (JsonObject)entry.Value!))
                .Where(item => Text(item.Subject, "Name") is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) && name != UnsetSubjectPlaceholder)
                .Select(item => new SubjectEntry { Id = item.Id, Name = Text(item.Subject, "Name")!, Teacher = NormalizeTeacherName(Text(item.Subject, "TeacherName")) })
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList(),
        };
        // 轮换周以学期开始日期所在周为第 1 周；未设置学期时以推算起点所在周（周一开始）为第 1 周。
        var rotationAnchor = SemesterWeeks.Monday(semesterStart ?? from);
        var rotationKnown = semesterStart is not null;
        for (var offset = 0; offset < days; offset++)
        {
            var date = from.AddDays(offset);
            var holiday = holidays?.Invoke(date);
            var isOff = holiday?.Kind == HolidayDayKinds.Off;
            JsonObject? plan = null;
            Guid? planId = null;
            if (!isOff)
            {
                // 调休上学日没有单独安排时，按插件 HolidayScheduleApplier 的规则补上同一周指定星期的课。
                var source = holiday is { Kind: HolidayDayKinds.Makeup, FollowWeekday: { } weekday } && OrderedPlan(profile, date) is null
                    ? date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).AddDays(weekday - 1)
                    : date;
                (plan, planId) = ResolvePlan(profile, source, rotationAnchor, ref usesRotation);
                if (rotationKnown) usesRotation = false;
            }
            var day = new ScheduleDay
            {
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ClassPlanName = plan is null ? null : Text(plan, "Name"),
                Enabled = plan is not null,
                Courses = plan is null ? [] : Courses(profile, plan, subjects),
                DayKind = holiday?.Kind switch
                {
                    HolidayDayKinds.Off => ScheduleDayKinds.Holiday,
                    HolidayDayKinds.Makeup => ScheduleDayKinds.Makeup,
                    _ => null,
                },
                HolidayName = holiday?.Name,
            };
            day.Revision = ProfileRevision(day, planId);
            bundle.Days.Add(day);
        }
        return new ProfileScheduleResult(bundle, usesRotation);
    }

    /// <summary>OrderedSchedules 中该日期指向的课表；临时层仅在档案开启临时层时生效（与宿主一致）。</summary>
    private static (JsonObject Plan, Guid Id)? OrderedPlan(JsonObject profile, DateOnly date)
    {
        if (Get(profile, "OrderedSchedules") is not JsonObject ordered) return null;
        var plans = Dictionary(profile, "ClassPlans");
        var overlayEnabled = Get(profile, "IsOverlayClassPlanEnabled") is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
        foreach (var key in DateKeys(ordered, date))
            if (ordered[key] is JsonObject schedule && ReferenceGuid(schedule, "ClassPlanId") is { } id &&
                FindByGuid(plans, id.ToString()) is JsonObject plan && (!IsOverlay(plan) || overlayEnabled))
                return (plan, id);
        return null;
    }

    private static (JsonObject? Plan, Guid? Id) ResolvePlan(JsonObject profile, DateOnly date, DateOnly rotationAnchor, ref bool usesRotation)
    {
        if (OrderedPlan(profile, date) is { } ordered) return (ordered.Plan, ordered.Id);

        var groups = Dictionary(profile, "ClassPlanGroups");
        var selectedGroup = ReferenceGuid(profile, "SelectedClassPlanGroupId") ?? DefaultClassPlanGroupId;
        Guid? tempGroup = null;
        if (Get(profile, "IsTempClassPlanGroupEnabled") is JsonValue tempValue && tempValue.TryGetValue<bool>(out var tempEnabled) && tempEnabled &&
            ReferenceGuid(profile, "TempClassPlanGroupId") is { } tempId &&
            (Text(profile, "TempClassPlanGroupExpireTime") is not { Length: >= 10 } expire ||
             !DateOnly.TryParseExact(expire[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expireDate) ||
             date <= expireDate))
            tempGroup = tempId;

        // 课表群优先级：临时课表群 > 当前选中的课表群 > 全局课表群；同一优先级内取轮换匹配的第一张。
        int GroupRank(JsonObject plan)
        {
            var group = Guid.TryParse(Text(plan, "AssociatedGroup"), out var id) ? id : DefaultClassPlanGroupId;
            if (tempGroup == group) return 0;
            if (selectedGroup == group) return 1;
            var isGlobal = group == Guid.Empty || FindByGuid(groups, group.ToString()) is JsonObject definition &&
                Get(definition, "IsGlobal") is JsonValue flag && flag.TryGetValue<bool>(out var global) && global;
            return isGlobal ? 2 : -1;
        }

        var candidates = Dictionary(profile, "ClassPlans")
            .Where(entry => Guid.TryParse(entry.Key, out _) && entry.Value is JsonObject)
            .Select(entry => (Id: Guid.Parse(entry.Key), Plan: (JsonObject)entry.Value!))
            .Where(item => !IsOverlay(item.Plan) &&
                           !(Get(item.Plan, "IsEnabled") is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var planEnabled) && !planEnabled))
            .Select(item => (item.Id, item.Plan, Rule: Get(item.Plan, "TimeRule") as JsonObject ?? new JsonObject(), Rank: GroupRank(item.Plan)))
            .Where(item => item.Rank >= 0 && Integer(item.Rule, "WeekDay", 0) == (int)date.DayOfWeek)
            .ToList();
        if (candidates.Any(item => Integer(item.Rule, "WeekCountDiv", 0) > 0)) usesRotation = true;
        var week = SemesterWeeks.WeekIndex(rotationAnchor, date);
        var match = candidates
            .Where(item => SemesterWeeks.InRotation(
                Integer(item.Rule, "WeekCountDiv", 0), Integer(item.Rule, "WeekCountDivTotal", 2), week))
            .OrderBy(item => item.Rank)
            .FirstOrDefault();
        return match.Plan is null ? (null, null) : (match.Plan, match.Id);
    }

    private static List<CourseEntry> Courses(JsonObject profile, JsonObject plan, JsonObject subjects)
    {
        var layout = ReferenceGuid(plan, "TimeLayoutId") is { } layoutId
            ? FindByGuid(Dictionary(profile, "TimeLayouts"), layoutId.ToString()) as JsonObject
            : null;
        var lessons = layout is null ? [] : Points(layout).Where(point => Integer(point, "TimeType", 0) == 0).ToList();
        var classes = (Get(plan, "Classes") as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        return classes.Select((course, index) =>
        {
            var subjectId = ReferenceGuid(course, "SubjectId") ?? Guid.Empty;
            var subject = FindByGuid(subjects, subjectId.ToString()) as JsonObject;
            var name = subject is null ? null : Text(subject, "Name");
            var point = index < lessons.Count ? lessons[index] : null;
            return new CourseEntry
            {
                Index = index,
                Label = $"第 {index + 1} 节",
                SubjectId = subjectId,
                Subject = name is not null and not UnsetSubjectPlaceholder ? name : "未设置",
                StartTime = point is not null && TryReadTime(point, true, out var start) ? start.ToString("hh\\:mm", CultureInfo.InvariantCulture) : null,
                EndTime = point is not null && TryReadTime(point, false, out var end) ? end.ToString("hh\\:mm", CultureInfo.InvariantCulture) : null,
                Teacher = subject is null ? null : NormalizeTeacherName(Text(subject, "TeacherName")),
                Enabled = !(Get(course, "IsEnabled") is JsonValue value && value.TryGetValue<bool>(out var enabled) && !enabled),
            };
        }).ToList();
    }

    private static string? NormalizeTeacherName(string? teacherName)
    {
        var name = teacherName?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>档案推算课表的修订号带 profile: 前缀，不会与插件计算的修订号相同，因此不能据此向插件提交换课。</summary>
    private static string ProfileRevision(ScheduleDay day, Guid? planId)
    {
        var canonical = new StringBuilder("profile|").Append(day.Date).Append('|').Append(planId).Append('|').Append(day.Enabled);
        foreach (var course in day.Courses)
            canonical.Append('|').Append(course.Index).Append(':').Append(course.SubjectId).Append(':').Append(course.Enabled)
                .Append(':').Append(course.StartTime).Append(':').Append(course.EndTime);
        return "profile:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16].ToLowerInvariant();
    }
}
