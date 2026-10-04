using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>老师按显示名绑定到的一个任教班级及其课表缓存。</summary>
public sealed record TaughtClass(
    Guid ClassId,
    string ClassName,
    bool VisitorEnabled,
    bool HasAvatar,
    ScheduleBundle Bundle);

/// <summary>
/// 老师与课表课程的绑定：老师账号（全局角色 Kind=Teacher）的显示名与班级课表中的科目教师名
/// （ClassIsland 档案 Subject.TeacherName，随 ScheduleBundle 推送）一致时，绑定到对应课程。
/// 课表由插件推送、服务端仅保留内存缓存，因此绑定完全动态计算：不落库、不迁移；
/// 班级插件尚未推送课表时绑定暂不可见，收到课表后自动生效。
/// </summary>
public sealed class TeacherBindingService(AppDbContext db, IStateStore state)
{
    /// <summary>教师字段中的姓名分隔符：课表里的教师名可能是“张三/李四”这类多人列表。</summary>
    private static readonly char[] NameSeparators = ['、', ',', '，', '/', ';', '；', '|'];

    /// <summary>
    /// 老师姓名与科目教师名匹配：去首尾空白后完全相等，或教师字段按分隔符拆分后任一姓名完全相等。
    /// 姓名为空、教师字段为空都不匹配。
    /// </summary>
    public static bool Matches(string? boundName, string? teacherField)
    {
        var name = boundName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        var field = teacherField?.Trim();
        if (string.IsNullOrEmpty(field)) return false;
        if (string.Equals(name, field, StringComparison.Ordinal)) return true;
        return field.Split(NameSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(name, StringComparer.Ordinal);
    }

    /// <summary>老师显示名绑定的任教班级（至少有一节匹配课程），按班级创建顺序返回。</summary>
    public async Task<IReadOnlyList<TaughtClass>> GetTaughtClassesAsync(
        string? boundName, CancellationToken ct = default)
    {
        var name = boundName?.Trim();
        if (string.IsNullOrEmpty(name)) return [];
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；班级数量极少，取回后内存排序。
        var classrooms = await db.Classrooms.AsNoTracking().ToListAsync(ct);
        var result = new List<TaughtClass>();
        foreach (var classroom in classrooms.OrderBy(x => x.CreatedAt))
        {
            var bundle = state.GetLatestSchedule(classroom.Id);
            if (bundle is null || !HasMatchedCourse(name, bundle)) continue;
            result.Add(new TaughtClass(classroom.Id, classroom.Name, classroom.VisitorAccessEnabled, classroom.Avatar != null, bundle));
        }
        return result;
    }

    /// <summary>用户是否按显示名绑定到指定班级的课表。</summary>
    public bool IsTaughtClass(string? boundName, Guid classId)
    {
        var name = boundName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        return HasMatchedCourse(name, state.GetLatestSchedule(classId));
    }

    /// <summary>某班级当前课表匹配到的“老师”角色用户 Id 集合；供授权镜像检测绑定变化。</summary>
    public async Task<IReadOnlySet<Guid>> GetMatchedTeacherUserIdsAsync(Guid classId, CancellationToken ct = default)
    {
        var teacherFields = state.GetLatestSchedule(classId)?.Days
            .SelectMany(x => x.Courses)
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Teacher))
            .Select(x => x.Teacher!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (teacherFields is not { Count: > 0 }) return new HashSet<Guid>();
        var teachers = await db.Users.AsNoTracking()
            .Where(x => x.Enabled && x.RoleDefinitionId == AccountRole.TeacherId)
            .Select(x => new { x.Id, x.DisplayName })
            .ToListAsync(ct);
        return teachers.Where(x => teacherFields.Any(field => Matches(x.DisplayName, field)))
            .Select(x => x.Id)
            .ToHashSet();
    }

    /// <summary>把当前用户任教班级的课表聚合为“我的日程”：按日期排序，每天按班级分组、节次排序。</summary>
    public async Task<MyScheduleResponse> BuildMyScheduleAsync(string? boundName, CancellationToken ct = default)
    {
        var response = new MyScheduleResponse();
        var taught = await GetTaughtClassesAsync(boundName, ct);
        if (taught.Count == 0) return response;
        response.FromDate = taught.Min(x => x.Bundle.FromDate)!;
        var dates = taught.SelectMany(x => x.Bundle.Days)
            .Select(x => x.Date)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var items = new List<MyScheduleItem>();
            foreach (var taughtClass in taught)
            {
                var courses = taughtClass.Bundle.Days
                    .Where(x => x.Date == date)
                    .SelectMany(x => x.Courses)
                    .Where(x => x.Enabled && Matches(boundName, x.Teacher))
                    .OrderBy(x => x.Index)
                    .ToList();
                if (courses.Count > 0)
                    items.Add(new MyScheduleItem { ClassId = taughtClass.ClassId, ClassName = taughtClass.ClassName, Courses = courses });
            }
            if (items.Count > 0) response.Days.Add(new MyScheduleDay { Date = date, Items = items });
        }
        return response;
    }

    /// <summary>
    /// 在“我的日程”中找出指定时刻正在上的课与下一节课。课程时间是教室电脑的本地时间，
    /// 按该班最近一次状态快照携带的时区偏移换算；尚无快照时退回服务端本地时区。
    /// 没有起始时间或日期无法解析的课程不参与计算。
    /// </summary>
    public async Task<MyNextCourseResponse> BuildMyNextCourseAsync(
        string? boundName, DateTimeOffset now, CancellationToken ct = default)
    {
        var slots = new List<MyCourseSlot>();
        foreach (var taughtClass in await GetTaughtClassesAsync(boundName, ct))
        {
            var offset = state.GetLatestSnapshot(taughtClass.ClassId)?.TimeZoneOffsetMinutes is { } minutes
                ? TimeSpan.FromMinutes(minutes)
                : TimeZoneInfo.Local.GetUtcOffset(now);
            foreach (var day in taughtClass.Bundle.Days)
            {
                if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                foreach (var course in day.Courses.Where(x => x.Enabled && Matches(boundName, x.Teacher)))
                {
                    if (!TimeOnly.TryParse(course.StartTime, CultureInfo.InvariantCulture, out var start)) continue;
                    var startsAt = new DateTimeOffset(date.ToDateTime(start), offset);
                    var endsAt = TimeOnly.TryParse(course.EndTime, CultureInfo.InvariantCulture, out var end)
                        ? new DateTimeOffset(date.ToDateTime(end), offset)
                        : startsAt;
                    slots.Add(new MyCourseSlot
                    {
                        Date = day.Date,
                        ClassId = taughtClass.ClassId,
                        ClassName = taughtClass.ClassName,
                        Course = course,
                        StartsAt = startsAt,
                        EndsAt = endsAt,
                    });
                }
            }
        }
        return new MyNextCourseResponse
        {
            At = now,
            Current = slots.Where(x => x.StartsAt <= now && now < x.EndsAt).MinBy(x => x.StartsAt),
            Next = slots.Where(x => x.StartsAt > now).MinBy(x => x.StartsAt),
        };
    }

    private static bool HasMatchedCourse(string name, ScheduleBundle? bundle) => bundle?.Days
        .SelectMany(x => x.Courses)
        .Any(x => x.Enabled && Matches(name, x.Teacher)) == true;
}
