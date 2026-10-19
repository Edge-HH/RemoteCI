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
/// 班级插件尚未推送课表时绑定（任教班级的访问权限）暂不可见，收到课表后自动生效。
/// “我的日程”与下一节课另外纳入插件从未同步过课表的班级：按该班当前档案推算（只读，不授予班级访问权）。
/// </summary>
public sealed class TeacherBindingService(AppDbContext db, IStateStore state, ScheduleViewService scheduleViews)
{
    /// <summary>
    /// 教师字段中的姓名分隔符：一个科目由多位老师任教时，课表里的教师名写成“张三/李四”这类多人列表，
    /// 每位老师各自按姓名对应到自己的个人日程。半角与全角写法都识别；空格不作分隔（英文姓名含空格）。
    /// </summary>
    private static readonly char[] NameSeparators =
        ['/', '／', '\\', '＼', '、', ',', '，', ';', '；', '|', '｜', '&', '＆', '+', '＋'];

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

    /// <summary>把教师字段拆成各位老师的姓名（课时统计按人计数）；空字段返回空列表。</summary>
    public static IReadOnlyList<string> SplitNames(string? teacherField) =>
        string.IsNullOrWhiteSpace(teacherField)
            ? []
            : teacherField.Split(NameSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToList();

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
            if (bundle is null || !IsTaught(name, classroom.Id, bundle)) continue;
            result.Add(new TaughtClass(classroom.Id, classroom.Name, classroom.VisitorAccessEnabled, classroom.Avatar != null, bundle));
        }
        return result;
    }

    /// <summary>用户是否按显示名绑定到指定班级的课表。</summary>
    public bool IsTaughtClass(string? boundName, Guid classId)
    {
        var name = boundName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        return state.GetLatestSchedule(classId) is { } bundle && IsTaught(name, classId, bundle);
    }

    /// <summary>
    /// 原课表或叠加换课临时任课老师后的课表任一匹配即视为任教：临时代上某班的老师在覆盖有效期间
    /// 也能看到并操作该班，被换走一节课的老师也不会因此暂时失去本班访问。
    /// </summary>
    private bool IsTaught(string name, Guid classId, ScheduleBundle overlaid) =>
        HasMatchedCourse(name, overlaid) || HasMatchedCourse(name, state.GetSourceSchedule(classId));

    /// <summary>某班级当前课表匹配到的“老师”角色用户 Id 集合；供授权镜像检测绑定变化。</summary>
    public async Task<IReadOnlySet<Guid>> GetMatchedTeacherUserIdsAsync(Guid classId, CancellationToken ct = default)
    {
        var teacherFields = new[] { state.GetLatestSchedule(classId), state.GetSourceSchedule(classId) }
            .Where(x => x is not null)
            .SelectMany(x => x!.Days)
            .SelectMany(x => x.Courses)
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Teacher))
            .Select(x => x.Teacher!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (teacherFields.Count == 0) return new HashSet<Guid>();
        var teachers = await db.Users.AsNoTracking()
            .Where(x => x.Enabled && x.RoleDefinitionId == AccountRole.TeacherId)
            .Select(x => new { x.Id, x.DisplayName })
            .ToListAsync(ct);
        return teachers.Where(x => teacherFields.Any(field => Matches(x.DisplayName, field)))
            .Select(x => x.Id)
            .ToHashSet();
    }

    /// <summary>
    /// “我的日程”的数据来源：插件同步过课表的班级用最后一次同步的课表（与任教班级绑定一致）；
    /// 插件从未同步过课表的班级按当前班级档案推算，Source 标记为 profile。
    /// <paramref name="fallbackOffset"/> 是没有任何班级快照时使用的时区（客户端上报的本机时区），见 <see cref="ClassClock"/>。
    /// </summary>
    private async Task<IReadOnlyList<(TaughtClass Class, string? Source)>> GetScheduledClassesAsync(
        string? boundName, TimeSpan? fallbackOffset, CancellationToken ct)
    {
        var name = boundName?.Trim();
        if (string.IsNullOrEmpty(name)) return [];
        var classrooms = (await db.Classrooms.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.CreatedAt).ToList();
        var estimated = await scheduleViews.BuildFromProfilesAsync(classrooms.Select(x => x.Id).ToList(), fallbackOffset, ct);
        var result = new List<(TaughtClass, string?)>();
        foreach (var classroom in classrooms)
        {
            TaughtClass Taught(ScheduleBundle schedule) =>
                new(classroom.Id, classroom.Name, classroom.VisitorAccessEnabled, classroom.Avatar != null, schedule);
            if (state.GetLatestSchedule(classroom.Id) is { } bundle)
            {
                if (IsTaught(name, classroom.Id, bundle)) result.Add((Taught(bundle), null));
            }
            else if (estimated.GetValueOrDefault(classroom.Id) is { } view && HasMatchedCourse(name, view.Bundle))
                result.Add((Taught(view.Bundle), ScheduleBundleSources.Profile));
        }
        return result;
    }

    /// <summary>把当前用户任教班级的课表聚合为“我的日程”：按日期排序，每天按班级分组、节次排序。</summary>
    public async Task<MyScheduleResponse> BuildMyScheduleAsync(
        string? boundName, TimeSpan? fallbackOffset = null, CancellationToken ct = default)
    {
        var response = new MyScheduleResponse();
        var taught = await GetScheduledClassesAsync(boundName, fallbackOffset, ct);
        if (taught.Count == 0) return response;
        response.FromDate = taught.Min(x => x.Class.Bundle.FromDate)!;
        var dates = taught.SelectMany(x => x.Class.Bundle.Days)
            .Select(x => x.Date)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var items = new List<MyScheduleItem>();
            foreach (var (taughtClass, source) in taught)
            {
                var courses = taughtClass.Bundle.Days
                    .Where(x => x.Date == date)
                    .SelectMany(x => x.Courses)
                    .Where(x => x.Enabled && Matches(boundName, x.Teacher))
                    .OrderBy(x => x.Index)
                    .ToList();
                if (courses.Count > 0)
                    items.Add(new MyScheduleItem
                    {
                        ClassId = taughtClass.ClassId, ClassName = taughtClass.ClassName, Courses = courses, Source = source,
                        TimeZoneOffsetMinutes = (int)ClassClock.Offset(state, taughtClass.ClassId, response.GeneratedAt, fallbackOffset).TotalMinutes,
                    });
            }
            if (items.Count > 0) response.Days.Add(new MyScheduleDay { Date = date, Items = items });
        }
        return response;
    }

    /// <summary>
    /// 在“我的日程”中找出指定时刻正在上的课与下一节课。课程时间是教室电脑的本地时间，按 <see cref="ClassClock"/>
    /// 的规则换算：该班状态快照的时区偏移，其次任一班级快照的偏移（按档案推算的班级没有自己的快照），
    /// 再次 <paramref name="fallbackOffset"/>（客户端上报的本机时区），最后服务端本地时区。
    /// 没有起始时间或日期无法解析的课程不参与计算。
    /// </summary>
    public async Task<MyNextCourseResponse> BuildMyNextCourseAsync(
        string? boundName, DateTimeOffset now, TimeSpan? fallbackOffset = null, CancellationToken ct = default)
    {
        var slots = new List<MyCourseSlot>();
        foreach (var (taughtClass, source) in await GetScheduledClassesAsync(boundName, fallbackOffset, ct))
        {
            var offset = ClassClock.Offset(state, taughtClass.ClassId, now, fallbackOffset);
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
                        Source = source,
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
