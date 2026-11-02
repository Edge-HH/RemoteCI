using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 按周浏览课表（GET /api/schedule/week、/api/me/schedule/week 与 WebUI 课表、日程页）：
/// 已过去的日子取课时台账（实际上过的课，含代课），今天及以后取插件课表或档案推算（与课表页同一来源）。
/// 可浏览范围从学期开始所在周的周一（未设置学期时为本周一）到课表数据的最后一天。
/// </summary>
public sealed class ScheduleWeekService(
    AppDbContext db,
    IStateStore state,
    ScheduleViewService views,
    TeacherBindingService teachers,
    PersonalScheduleService personal,
    SemesterSettingsService semester,
    HolidayCalendarService holidays,
    TimeProvider time)
{
    /// <summary>一次最多查询的天数（6 周）。</summary>
    public const int MaxRangeDays = 42;

    /// <summary>某班 [from, to] 的生效课表；<paramref name="from"/> 省略时为本周一，<paramref name="to"/> 省略时为 from 起 7 天。</summary>
    public async Task<ScheduleWeekResponse> BuildClassAsync(Guid classId, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var today = ClassClock.Today(state, classId, now);
        var view = await views.GetAsync(classId, ct, ScheduleBundle.HorizonDays);
        var bundle = view?.Bundle;
        var semesterStart = await semester.GetStartAsync(ct);
        var (start, end) = Range(from, to, today);
        var minDate = MinDate(semesterStart, today);
        var maxDate = LastDate(bundle) is { } last && last >= today ? last : today;
        var calendar = await HolidayLookupAsync(ct);
        var ledger = await LedgerAsync(classId, Max(start, minDate), Min(end, today.AddDays(-1)), ct);
        var adjustments = await PastAdjustmentsAsync(classId, Max(start, minDate), Min(end, today.AddDays(-1)), ct);
        var subjects = bundle?.Subjects ?? [];

        var days = new List<ScheduleDay>();
        for (var date = Max(start, minDate); date <= Min(end, maxDate); date = date.AddDays(1))
        {
            var key = SemesterWeeks.Format(date);
            var live = bundle?.Days.FirstOrDefault(x => x.Date == key);
            ScheduleDay day;
            if (date < today && ledger.TryGetValue(key, out var rows))
            {
                day = new ScheduleDay
                {
                    Date = key, Enabled = true,
                    Courses = rows.Select(row => FromLedger(row, subjects, adjustments)).ToList(),
                };
                day.DividerAfterIndexes = BorrowDividers(day, bundle);
            }
            else if (live is not null) day = live;
            else day = new ScheduleDay { Date = key };
            if (calendar(date) is { } holiday && day.DayKind is null)
            {
                day.DayKind = holiday.Kind == HolidayDayKinds.Off ? ScheduleDayKinds.Holiday : ScheduleDayKinds.Makeup;
                day.HolidayName = holiday.Name;
            }
            days.Add(day);
        }
        return new ScheduleWeekResponse
        {
            ClassId = classId,
            From = SemesterWeeks.Format(start),
            To = SemesterWeeks.Format(end),
            Today = SemesterWeeks.Format(today),
            MinDate = SemesterWeeks.Format(minDate),
            MaxDate = SemesterWeeks.Format(maxDate),
            TeachingWeek = semesterStart is { } semesterDate ? SemesterWeeks.TeachingWeek(semesterDate, start) : null,
            Source = view?.Source == ScheduleViewSource.Profile ? ScheduleBundleSources.Profile : null,
            Periods = ScheduleTimeAxis.Build(days.Select(x => new ScheduleTimeAxis.Sample(x.Courses, x.DividerAfterIndexes))),
            Days = days,
            Subjects = subjects,
        };
    }

    /// <summary>
    /// 当前用户 [from, to] 的“我的日程”：班级课程按显示名匹配科目教师（过去的日子匹配课时台账的实际上课老师），加上自定义课程。
    /// 节次轴取这些课所在班级当天的完整时间表，因此老师不上的节次也会在表格中留出一行。
    /// </summary>
    public async Task<MyScheduleWeekResponse> BuildMineAsync(
        Guid userId, string? boundName, DateOnly? from = null, DateOnly? to = null, TimeSpan? fallbackOffset = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var today = ClassClock.Today(state, Guid.Empty, now, fallbackOffset);
        var semesterStart = await semester.GetStartAsync(ct);
        var (start, end) = Range(from, to, today);
        var minDate = MinDate(semesterStart, today);
        var maxDate = today.AddDays(ScheduleBundle.HorizonDays - 1);
        var first = Max(start, minDate);
        var last = Min(end, maxDate);
        var calendar = await HolidayLookupAsync(ct);
        var classes = boundName is null
            ? new List<(TaughtClass Class, string? Source)>()
            : await teachers.GetDisplayClassesAsync(boundName, fallbackOffset, ct);
        var subjectsByClass = classes.ToDictionary(x => x.Class.ClassId, x => (IReadOnlyList<SubjectEntry>)x.Class.Bundle.Subjects);
        var classNames = (await db.Classrooms.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);

        // 过去的日子：所有班级的课时台账中由本人上的课；同班同日的全部台账作为节次轴的完整时间表。
        var pastRows = boundName is null || first > Min(last, today.AddDays(-1))
            ? new List<LessonLedgerEntry>()
            : await LedgerRowsAsync(null, first, Min(last, today.AddDays(-1)), ct);
        var samples = new List<ScheduleTimeAxis.Sample>();
        var itemsByDate = new Dictionary<string, List<MyScheduleItem>>(StringComparer.Ordinal);
        foreach (var group in pastRows.GroupBy(x => (x.Date, x.ClassId)).OrderBy(x => x.Key.Date, StringComparer.Ordinal))
        {
            var mine = group.Where(x => TeacherBindingService.Matches(boundName, x.TeacherName)).ToList();
            if (mine.Count == 0) continue;
            var subjects = subjectsByClass.GetValueOrDefault(group.Key.ClassId) ?? [];
            var full = group.Select(row => FromLedger(row, subjects, null)).ToList();
            samples.Add(new ScheduleTimeAxis.Sample(full));
            Add(itemsByDate, group.Key.Date, new MyScheduleItem
            {
                ClassId = group.Key.ClassId,
                ClassName = classNames.GetValueOrDefault(group.Key.ClassId) ?? mine[0].ClassName,
                Courses = mine.Select(row => FromLedger(row, subjects, null)).ToList(),
            });
        }

        // 今天及以后：插件课表或档案推算中本人的课。
        foreach (var (taught, source) in classes)
        {
            foreach (var day in taught.Bundle.Days)
            {
                if (SemesterWeeks.Parse(day.Date) is not { } date || date < today || date < first || date > last) continue;
                var courses = day.Courses.Where(x => x.Enabled && TeacherBindingService.Matches(boundName, x.Teacher)).OrderBy(x => x.Index).ToList();
                if (courses.Count == 0) continue;
                samples.Add(new ScheduleTimeAxis.Sample(day.Courses, day.DividerAfterIndexes));
                Add(itemsByDate, day.Date, new MyScheduleItem
                {
                    ClassId = taught.ClassId, ClassName = taught.ClassName, Courses = courses, Source = source,
                    DividerAfterIndexes = day.DividerAfterIndexes,
                    TimeZoneOffsetMinutes = (int)ClassClock.Offset(state, taught.ClassId, now, fallbackOffset).TotalMinutes,
                });
            }
        }

        if (first <= last)
            foreach (var (date, item) in await personal.OccurrenceItemsAsync(userId, first, last, ct))
                Add(itemsByDate, SemesterWeeks.Format(date), item);

        var days = new List<MyScheduleDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var key = SemesterWeeks.Format(date);
            var day = new MyScheduleDay { Date = key, Items = itemsByDate.GetValueOrDefault(key) ?? [] };
            if (calendar(date) is { } holiday)
            {
                day.DayKind = holiday.Kind == HolidayDayKinds.Off ? ScheduleDayKinds.Holiday : ScheduleDayKinds.Makeup;
                day.HolidayName = holiday.Name;
            }
            days.Add(day);
        }
        return new MyScheduleWeekResponse
        {
            From = SemesterWeeks.Format(start),
            To = SemesterWeeks.Format(end),
            Today = SemesterWeeks.Format(today),
            MinDate = SemesterWeeks.Format(minDate),
            MaxDate = SemesterWeeks.Format(maxDate),
            TeachingWeek = semesterStart is { } semesterDate ? SemesterWeeks.TeachingWeek(semesterDate, start) : null,
            Periods = ScheduleTimeAxis.Build(samples),
            Days = days,
        };
    }

    /// <summary>请求范围：from 省略时为本周一，to 省略时为 from 起 7 天。范围校验由 <see cref="Validate"/> 负责。</summary>
    public static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var start = from ?? SemesterWeeks.Monday(today);
        return (start, to ?? start.AddDays(6));
    }

    /// <summary>校验查询参数；通过返回 null，否则返回面向调用方的错误说明。</summary>
    public static string? Validate(string? from, string? to, out DateOnly? fromDate, out DateOnly? toDate)
    {
        fromDate = SemesterWeeks.Parse(from);
        toDate = SemesterWeeks.Parse(to);
        if (!string.IsNullOrWhiteSpace(from) && fromDate is null) return "from 需为 yyyy-MM-dd";
        if (!string.IsNullOrWhiteSpace(to) && toDate is null) return "to 需为 yyyy-MM-dd";
        if (toDate is not null && fromDate is null) return "指定 to 时也需指定 from";
        if (fromDate is { } start && toDate is { } end && (end < start || end.DayNumber - start.DayNumber + 1 > MaxRangeDays))
            return $"日期范围需为 1–{MaxRangeDays} 天";
        return null;
    }

    private static DateOnly MinDate(DateOnly? semesterStart, DateOnly today)
    {
        var thisWeek = SemesterWeeks.Monday(today);
        return semesterStart is { } start && SemesterWeeks.Monday(start) < thisWeek ? SemesterWeeks.Monday(start) : thisWeek;
    }

    private static DateOnly? LastDate(ScheduleBundle? bundle) =>
        bundle?.Days.Select(x => SemesterWeeks.Parse(x.Date)).Where(x => x is not null).Max();

    private async Task<Func<DateOnly, HolidayCalendarDay?>> HolidayLookupAsync(CancellationToken ct)
    {
        var calendar = await holidays.BuildCalendarAsync(ct);
        if (!calendar.Enabled) return _ => null;
        var days = calendar.Days.GroupBy(x => x.Date, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        return date => days.GetValueOrDefault(SemesterWeeks.Format(date));
    }

    private async Task<Dictionary<string, List<LessonLedgerEntry>>> LedgerAsync(Guid classId, DateOnly from, DateOnly to, CancellationToken ct) =>
        from > to
            ? new Dictionary<string, List<LessonLedgerEntry>>(StringComparer.Ordinal)
            : (await LedgerRowsAsync(classId, from, to, ct)).GroupBy(x => x.Date, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.OrderBy(row => row.Index).ToList(), StringComparer.Ordinal);

    private async Task<List<LessonLedgerEntry>> LedgerRowsAsync(Guid? classId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var first = SemesterWeeks.Format(from);
        var last = SemesterWeeks.Format(to);
        var query = db.LessonLedgerEntries.AsNoTracking()
            .Where(x => string.Compare(x.Date, first) >= 0 && string.Compare(x.Date, last) <= 0);
        if (classId is { } id) query = query.Where(x => x.ClassId == id);
        return (await query.ToListAsync(ct)).OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Index).ToList();
    }

    /// <summary>过去日期每个课位最上层的已生效调课（日期过后记录转为 Expired），用于在台账课程上标出“换”。</summary>
    private async Task<Dictionary<(string Date, int Index), ScheduleAdjustment>> PastAdjustmentsAsync(
        Guid classId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to) return new();
        var first = SemesterWeeks.Format(from);
        var last = SemesterWeeks.Format(to);
        var rows = await db.ScheduleAdjustments.AsNoTracking()
            .Where(x => x.ClassId == classId && string.Compare(x.Date, first) >= 0 && string.Compare(x.Date, last) <= 0 &&
                        (x.Status == ScheduleAdjustmentStatus.Active || x.Status == ScheduleAdjustmentStatus.Expired))
            .ToListAsync(ct);
        return rows.GroupBy(x => (x.Date, x.Index)).ToDictionary(x => x.Key, x => x.MaxBy(row => row.Sequence)!);
    }

    private static CourseEntry FromLedger(
        LessonLedgerEntry row, IReadOnlyList<SubjectEntry> subjects, IReadOnlyDictionary<(string Date, int Index), ScheduleAdjustment>? adjustments)
    {
        var subject = subjects.FirstOrDefault(x => x.Name == row.Subject);
        var adjustment = adjustments?.GetValueOrDefault((row.Date, row.Index));
        return new CourseEntry
        {
            Index = row.Index,
            Label = string.IsNullOrEmpty(row.Label) ? $"第 {row.Index + 1} 节" : row.Label,
            SubjectId = subject?.Id ?? Guid.Empty,
            Subject = row.Subject,
            StartTime = row.StartTime,
            EndTime = row.EndTime,
            Teacher = string.IsNullOrWhiteSpace(row.TeacherName) ? null : row.TeacherName.Trim(),
            Location = subject?.Location,
            Enabled = true,
            AdjustmentId = adjustment is not null && adjustment.ToSubject == row.Subject ? adjustment.Id : null,
        };
    }

    /// <summary>台账不记录分割线：借用节次时间完全相同的课表日的分割线。</summary>
    private static List<int>? BorrowDividers(ScheduleDay day, ScheduleBundle? bundle)
    {
        static string Shape(IEnumerable<CourseEntry> courses) =>
            string.Join(';', courses.OrderBy(x => x.Index).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Index}:{x.StartTime}-{x.EndTime}")));
        var shape = Shape(day.Courses);
        return bundle?.Days.FirstOrDefault(x => x.DividerAfterIndexes is { Count: > 0 } && Shape(x.Courses) == shape)?.DividerAfterIndexes;
    }

    private static void Add(Dictionary<string, List<MyScheduleItem>> items, string date, MyScheduleItem item)
    {
        if (!items.TryGetValue(date, out var list)) items[date] = list = [];
        list.Add(item);
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;
    private static DateOnly Min(DateOnly left, DateOnly right) => left < right ? left : right;
}
