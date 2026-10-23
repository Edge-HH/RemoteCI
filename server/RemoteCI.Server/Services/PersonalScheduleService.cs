using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>一次具体的自定义课程：日期与换算成绝对时间的起止时刻。</summary>
public readonly record struct PersonalCourseOccurrence(DateOnly Date, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>所有查询及写入都以认证账号的 UserId 为边界，不按姓名或班级共享。</summary>
public sealed class PersonalScheduleService(AppDbContext db, TeacherBindingService teachers, SemesterSettingsService semester, TimeProvider time)
{
    public const string Source = "personal";
    /// <summary>“我的日程”展开自定义课程的天数，与班级课表的推算窗口一致。</summary>
    private const int WindowDays = 7;
    /// <summary>查找下一次课时向后看的天数：覆盖最长的多周轮换。</summary>
    private const int LookAheadDays = 7 * SemesterWeeks.MaxRotation + 7;

    public async Task<List<PersonalCourseView>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var start = await semester.GetStartAsync(ct);
        var now = time.GetUtcNow();
        return (await RowsAsync(userId, ct)).Select(row => ToView(row, start, now)).ToList();
    }

    public async Task<PersonalCourseView> SaveAsync(Guid userId, Guid? id, PersonalCourseRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Trim().Length > 100 ||
            (request.Location?.Trim().Length ?? 0) > 200)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "课程名称需为 1–100 字，地点最多 200 字");
        if (request.ReminderMinutes is < 1 or > 10080)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "提前提醒为 1–10080 分钟或 null（关闭）");
        var semesterStart = await semester.GetStartAsync(ct);
        var rule = Normalize(request, semesterStart, time.GetLocalNow().Offset);
        var row = id is null ? new PersonalCourse { Id = Guid.NewGuid(), UserId = userId } :
            await db.PersonalCourses.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "课程不存在");
        // 已提醒标记记录的是那次课的开始时刻：调整时间后自然按新时间提醒；只改名称或地点不会重复提醒，改提醒设置才重新排程。
        if (row.ReminderMinutes != request.ReminderMinutes) row.UpcomingSentFor = 0;
        if (row.NotifyAtStart != request.NotifyAtStart) row.StartSentFor = 0;
        row.Subject = request.Subject.Trim();
        row.Location = request.Location?.Trim() ?? "";
        (row.WeekDay, row.StartMinutes, row.EndMinutes, row.WeekCountDiv, row.WeekCountDivTotal, row.Date, row.TimeZoneOffsetMinutes) = rule;
        row.ReminderMinutes = request.ReminderMinutes;
        row.NotifyAtStart = request.NotifyAtStart;
        if (id is null) db.PersonalCourses.Add(row);
        await db.SaveChangesAsync(ct);
        return ToView(row, semesterStart, time.GetUtcNow());
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        if (await db.PersonalCourses.Where(x => x.UserId == userId && x.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "课程不存在");
    }

    public async Task<MyScheduleResponse> BuildAsync(Guid userId, string? teacherName, TimeSpan? offset = null, CancellationToken ct = default)
    {
        var result = teacherName is null ? new MyScheduleResponse() : await teachers.BuildMyScheduleAsync(teacherName, offset, ct);
        var semesterStart = await semester.GetStartAsync(ct);
        var now = time.GetUtcNow();
        foreach (var row in await RowsAsync(userId, ct))
        {
            var today = LocalDate(row, now);
            foreach (var occurrence in Occurrences(row, today, today.AddDays(WindowDays - 1), semesterStart))
            {
                var date = SemesterWeeks.Format(occurrence.Date);
                var day = result.Days.FirstOrDefault(x => x.Date == date);
                if (day is null) result.Days.Add(day = new MyScheduleDay { Date = date });
                day.Items.Add(new MyScheduleItem
                {
                    ClassName = ClassName(row), Source = Source, TimeZoneOffsetMinutes = row.TimeZoneOffsetMinutes,
                    Courses = [ToEntry(row, occurrence)],
                });
            }
        }
        result.Days = result.Days.OrderBy(x => x.Date, StringComparer.Ordinal).ToList();
        if (result.Days.Count > 0) result.FromDate = result.Days[0].Date;
        return result;
    }

    public async Task<MyNextCourseResponse> NextAsync(Guid userId, string? teacherName, DateTimeOffset now, TimeSpan? offset = null, CancellationToken ct = default)
    {
        var result = teacherName is null ? new MyNextCourseResponse { At = now } : await teachers.BuildMyNextCourseAsync(teacherName, now, offset, ct);
        var semesterStart = await semester.GetStartAsync(ct);
        var slots = new List<MyCourseSlot>();
        foreach (var row in await RowsAsync(userId, ct))
        {
            var today = LocalDate(row, now);
            // 正在上的这一次与之后最近的一次（例如下周同一时间）都参与比较。
            var upcoming = Occurrences(row, today.AddDays(-1), today.AddDays(LookAheadDays), semesterStart)
                .Where(x => x.EndsAt > now).Take(2).Where((x, index) => index == 0 || x.StartsAt > now);
            slots.AddRange(upcoming.Select(value => new MyCourseSlot
            {
                Date = SemesterWeeks.Format(value.Date), ClassName = ClassName(row), Source = Source,
                Course = ToEntry(row, value), StartsAt = value.StartsAt, EndsAt = value.EndsAt,
            }));
        }
        if (result.Current is not null) slots.Add(result.Current);
        if (result.Next is not null) slots.Add(result.Next);
        result.Current = slots.Where(x => x.StartsAt <= now && now < x.EndsAt).MinBy(x => x.StartsAt);
        result.Next = slots.Where(x => x.StartsAt > now).MinBy(x => x.StartsAt);
        return result;
    }

    /// <summary>
    /// 课程在 [from, to] 内（按课程时区的本地日期）出现的每一次。轮换课程需要学期开始日期；管理员清除学期后暂不出现，
    /// 以免单双周课程在错误的周提醒。
    /// </summary>
    public static IEnumerable<PersonalCourseOccurrence> Occurrences(PersonalCourse row, DateOnly from, DateOnly to, DateOnly? semesterStart)
    {
        var offset = TimeSpan.FromMinutes(row.TimeZoneOffsetMinutes);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (row.Date is { } only ? date != only : (int)date.DayOfWeek != row.WeekDay) continue;
            if (row.Date is null && row.WeekCountDiv > 0 &&
                (semesterStart is not { } start || !SemesterWeeks.InRotation(row.WeekCountDiv, row.WeekCountDivTotal, SemesterWeeks.WeekIndex(start, date))))
                continue;
            yield return At(row, date);
        }
    }

    private static PersonalCourseOccurrence At(PersonalCourse row, DateOnly date)
    {
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromMinutes(row.TimeZoneOffsetMinutes));
        return new PersonalCourseOccurrence(date, midnight.AddMinutes(row.StartMinutes), midnight.AddMinutes(row.EndMinutes));
    }

    public static DateOnly LocalDate(PersonalCourse row, DateTimeOffset now) =>
        DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(row.TimeZoneOffsetMinutes)).DateTime);

    public static string FormatMinutes(int minutes) =>
        $"{minutes / 60:00}:{minutes % 60:00}";

    public static string Summary(PersonalCourse row)
    {
        var time = $"{FormatMinutes(row.StartMinutes)}–{FormatMinutes(row.EndMinutes)}";
        if (row.Date is { } date) return $"{SemesterWeeks.Format(date)}（{SemesterWeeks.WeekdayNames[(int)date.DayOfWeek]}）{time} · 单次";
        var summary = $"每{SemesterWeeks.WeekdayNames[row.WeekDay]} {time}";
        return row.WeekCountDiv > 0 ? summary + " · " + SemesterWeeks.RotationLabel(row.WeekCountDiv, row.WeekCountDivTotal) : summary;
    }

    internal PersonalCourseView ToView(PersonalCourse row, DateOnly? semesterStart, DateTimeOffset now)
    {
        // startsAt/endsAt 给出下一次（或正在进行的一次）课，兼容只认识单次课程的旧客户端；单次课程过期后仍返回它的日期。
        var today = LocalDate(row, now);
        var occurrence = Occurrences(row, today.AddDays(-1), today.AddDays(LookAheadDays), semesterStart)
            .Cast<PersonalCourseOccurrence?>().FirstOrDefault(x => x!.Value.EndsAt > now)
            ?? At(row, row.Date ?? today.AddDays((row.WeekDay - (int)today.DayOfWeek + 7) % 7));
        return new PersonalCourseView
        {
            Id = row.Id, Subject = row.Subject, Location = row.Location,
            WeekDay = row.WeekDay, StartTime = FormatMinutes(row.StartMinutes), EndTime = FormatMinutes(row.EndMinutes),
            WeekCountDiv = row.WeekCountDiv, WeekCountDivTotal = row.WeekCountDivTotal, TimeZoneOffsetMinutes = row.TimeZoneOffsetMinutes,
            Date = row.Date is { } date ? SemesterWeeks.Format(date) : null, Summary = Summary(row),
            StartsAt = occurrence.StartsAt, EndsAt = occurrence.EndsAt,
            ReminderMinutes = row.ReminderMinutes, NotifyAtStart = row.NotifyAtStart,
        };
    }

    private async Task<List<PersonalCourse>> RowsAsync(Guid userId, CancellationToken ct) =>
        (await db.PersonalCourses.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct))
            .OrderBy(x => x.Date.HasValue).ThenBy(x => x.Date).ThenBy(x => (x.WeekDay + 6) % 7).ThenBy(x => x.StartMinutes)
            .ThenBy(x => x.Subject, StringComparer.Ordinal).ToList();

    private static (int WeekDay, int Start, int End, int Div, int Total, DateOnly? Date, int Offset) Normalize(
        PersonalCourseRequest request, DateOnly? semesterStart, TimeSpan serverOffset)
    {
        if (request.WeekDay is { } weekDay)
        {
            if (weekDay is < 0 or > 6)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "星期需为 0（周日）到 6（周六）");
            if (ParseMinutes(request.StartTime) is not { } start || ParseMinutes(request.EndTime) is not { } end || end <= start)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请设置有效的起止时刻（HH:mm），结束需晚于开始且在同一天内");
            var offset = request.TimeZoneOffsetMinutes ?? (int)serverOffset.TotalMinutes;
            if (offset is < -840 or > 840)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "时区偏移无效");
            var div = request.WeekCountDiv;
            var total = div == 0 ? 2 : request.WeekCountDivTotal;
            if (!SemesterWeeks.IsValidRotation(div, total))
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"周次设置无效：每周，或 2–{SemesterWeeks.MaxRotation} 周轮换中的第几周");
            if (div > 0 && semesterStart is null)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "管理员尚未设置学期开始日期，暂不能按单双周或多周轮换安排，请先选择“每周”");
            return (weekDay, start, end, div, total, null, offset);
        }
        if (request.StartsAt is { } startsAt && request.EndsAt is { } endsAt)
        {
            // 旧客户端按起止时间创建单次课程：按开始时间所带的偏移记录本地日期与时刻。
            var localEnd = endsAt.ToOffset(startsAt.Offset);
            var date = DateOnly.FromDateTime(startsAt.DateTime);
            var start = (int)startsAt.TimeOfDay.TotalMinutes;
            var end = DateOnly.FromDateTime(localEnd.DateTime) == date ? (int)localEnd.TimeOfDay.TotalMinutes : -1;
            if (end <= start)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请设置有效的起止时间，单次课程需在当天内结束");
            return ((int)date.DayOfWeek, start, end, 0, 2, date, (int)startsAt.Offset.TotalMinutes);
        }
        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请选择星期几和起止时刻");
    }

    private static int? ParseMinutes(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.Hour * 60 + time.Minute
            : null;

    private static string ClassName(PersonalCourse row) => string.IsNullOrEmpty(row.Location) ? "自定义课程" : row.Location;

    private static CourseEntry ToEntry(PersonalCourse row, PersonalCourseOccurrence occurrence) => new()
    {
        PersonalCourseId = row.Id, Label = "自定义", Subject = row.Subject, Enabled = true,
        StartTime = FormatMinutes(row.StartMinutes), EndTime = FormatMinutes(row.EndMinutes),
        StartsAt = occurrence.StartsAt, EndsAt = occurrence.EndsAt,
        ReminderMinutes = row.ReminderMinutes, NotifyAtStart = row.NotifyAtStart,
    };
}
