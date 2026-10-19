using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>把已经结束的课写入课时台账；同一节课只写一次。</summary>
public sealed class LessonLedgerRecorder(
    AppDbContext db,
    IStateStore state,
    ScheduleViewService scheduleViews)
{
    public async Task<int> RecordAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var classes = await db.Classrooms.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var added = 0;
        foreach (var classroom in classes)
        {
            if (await scheduleViews.GetAsync(classroom.Id, ct) is not { } view) continue;
            var offset = ClassClock.Offset(state, classroom.Id, now);
            var today = ClassClock.Today(state, classroom.Id, now);
            var source = state.GetSourceSchedule(classroom.Id);
            // 也看昨天：深夜结束的课可能在跨天后的第一次检查才被发现。
            foreach (var date in new[] { today.AddDays(-1), today })
            {
                var day = ClassSchedulePeriods.FindDay(view.Bundle, date);
                var ended = ClassSchedulePeriods.Lessons(day, offset)
                    .Where(x => x.EndsAt <= now && !string.IsNullOrWhiteSpace(x.Course.Subject))
                    .ToList();
                if (ended.Count == 0) continue;
                var key = ClassSchedulePeriods.DateKey(date);
                var recorded = (await db.LessonLedgerEntries.AsNoTracking()
                    .Where(x => x.ClassId == classroom.Id && x.Date == key)
                    .Select(x => x.Index).ToListAsync(ct)).ToHashSet();
                var sourceDay = source is null ? null : ClassSchedulePeriods.FindDay(source, date);
                foreach (var lesson in ended.Where(x => !recorded.Contains(x.Course.Index)))
                {
                    var defaultTeacher = sourceDay?.Courses.FirstOrDefault(x => x.Index == lesson.Course.Index)?.Teacher
                        ?? lesson.Course.Teacher;
                    db.LessonLedgerEntries.Add(new LessonLedgerEntry
                    {
                        ClassId = classroom.Id,
                        ClassName = classroom.Name,
                        Date = key,
                        Index = lesson.Course.Index,
                        Label = lesson.Course.Label,
                        Subject = lesson.Course.Subject,
                        StartTime = lesson.Course.StartTime,
                        EndTime = lesson.Course.EndTime,
                        TeacherName = Blank(lesson.Course.Teacher),
                        DefaultTeacherName = Blank(defaultTeacher),
                        // 实际课表叠加了换课产生的单节临时任课老师；与插件原始课表不同即为代课。
                        IsSubstitute = !string.Equals(Blank(lesson.Course.Teacher), Blank(defaultTeacher), StringComparison.Ordinal),
                        Source = view.Source == ScheduleViewSource.Plugin ? LessonLedgerSource.ClassIsland : LessonLedgerSource.Profile,
                        RecordedAtUnixMs = now.ToUnixTimeMilliseconds(),
                    });
                    added++;
                }
            }
        }
        if (added > 0)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // 并发写入同一节课时唯一索引冲突：丢弃本轮，下一轮会补上没写进去的课。
                db.ChangeTracker.Clear();
                return 0;
            }
        }
        return added;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>每 5 分钟把已结束的课写入台账。</summary>
public sealed class LessonLedgerWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    ILogger<LessonLedgerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OperationsWorkers) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), timeProvider);
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<LessonLedgerRecorder>()
                        .RecordAsync(timeProvider.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "记录课时台账失败");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }
}

public sealed record ClassLessonCount(Guid ClassId, string ClassName, int Total);

/// <summary>一位老师的课时：Total 为实际上课节数（含代课），Substitute 为其中代别人上的，Substituted 为本该自己上、被别人代的。</summary>
public sealed record TeacherLessonStats(
    string Teacher, int Total, int Substitute, int Substituted, IReadOnlyList<ClassLessonCount> Classes);

public sealed record LessonStatsReport(
    string From, string To, bool IncludesProfile, int LessonCount, IReadOnlyList<TeacherLessonStats> Teachers);

/// <summary>按教师汇总课时台账；教师字段按与“我的日程”相同的分隔规则拆分，多位老师合上的课各计一节。</summary>
public sealed class LessonStatsService(AppDbContext db)
{
    public const int MaxRangeDays = 400;

    public async Task<LessonStatsReport> GetAsync(
        IReadOnlyList<Guid>? classScope, DateOnly from, DateOnly to, Guid? classId = null, string? teacher = null,
        bool includeProfile = false, CancellationToken ct = default)
    {
        var rows = await LoadAsync(classScope, from, to, classId, includeProfile, ct);
        var stats = new Dictionary<string, Tally>(StringComparer.Ordinal);
        Tally For(string name) => stats.TryGetValue(name, out var found) ? found : stats[name] = new Tally();
        foreach (var row in rows)
        {
            foreach (var name in TeacherBindingService.SplitNames(row.TeacherName))
            {
                var tally = For(name);
                tally.Total++;
                if (row.IsSubstitute) tally.Substitute++;
                tally.Classes[row.ClassId] = (row.ClassName, tally.Classes.GetValueOrDefault(row.ClassId).Total + 1);
            }
            if (!row.IsSubstitute) continue;
            foreach (var name in TeacherBindingService.SplitNames(row.DefaultTeacherName))
            {
                // 默认老师自己也在实际老师之列（例如多人合上的课只是换了合作老师）时不算被代课。
                if (TeacherBindingService.Matches(name, row.TeacherName)) continue;
                For(name).Substituted++;
            }
        }
        var teachers = stats
            .Where(x => teacher is null || TeacherBindingService.Matches(teacher, x.Key))
            .Select(x => new TeacherLessonStats(x.Key, x.Value.Total, x.Value.Substitute, x.Value.Substituted,
                x.Value.Classes.Select(c => new ClassLessonCount(c.Key, c.Value.Name, c.Value.Total))
                    .OrderByDescending(c => c.Total).ThenBy(c => c.ClassName, StringComparer.CurrentCulture).ToList()))
            .OrderByDescending(x => x.Total)
            .ThenBy(x => x.Teacher, StringComparer.CurrentCulture)
            .ToList();
        return new LessonStatsReport(ClassSchedulePeriods.DateKey(from), ClassSchedulePeriods.DateKey(to), includeProfile, rows.Count, teachers);
    }

    public async Task<byte[]> ExportAsync(
        IReadOnlyList<Guid>? classScope, DateOnly from, DateOnly to, Guid? classId = null, string? teacher = null,
        bool includeProfile = false, CancellationToken ct = default)
    {
        var report = await GetAsync(classScope, from, to, classId, teacher, includeProfile, ct);
        var rows = await LoadAsync(classScope, from, to, classId, includeProfile, ct);
        if (teacher is not null)
            rows = rows.Where(x => TeacherBindingService.Matches(teacher, x.TeacherName) ||
                (x.IsSubstitute && TeacherBindingService.Matches(teacher, x.DefaultTeacherName))).ToList();
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("汇总");
        string[] summaryHeaders = ["教师", "实际课时", "其中代课", "被代课", "分班级"];
        for (var i = 0; i < summaryHeaders.Length; i++) summary.Cell(1, i + 1).Value = summaryHeaders[i];
        var line = 2;
        foreach (var item in report.Teachers)
        {
            summary.Cell(line, 1).Value = item.Teacher;
            summary.Cell(line, 2).Value = item.Total;
            summary.Cell(line, 3).Value = item.Substitute;
            summary.Cell(line, 4).Value = item.Substituted;
            summary.Cell(line, 5).Value = string.Join("，", item.Classes.Select(x => $"{x.ClassName} {x.Total}"));
            line++;
        }
        var detail = workbook.AddWorksheet("明细");
        string[] detailHeaders = ["日期", "班级", "节次", "时间", "科目", "实际老师", "默认老师", "代课", "来源"];
        for (var i = 0; i < detailHeaders.Length; i++) detail.Cell(1, i + 1).Value = detailHeaders[i];
        line = 2;
        foreach (var row in rows.OrderBy(x => x.Date).ThenBy(x => x.ClassName, StringComparer.CurrentCulture).ThenBy(x => x.Index))
        {
            detail.Cell(line, 1).Value = row.Date;
            detail.Cell(line, 2).Value = row.ClassName;
            detail.Cell(line, 3).Value = row.Label;
            detail.Cell(line, 4).Value = $"{row.StartTime}–{row.EndTime}";
            detail.Cell(line, 5).Value = row.Subject;
            detail.Cell(line, 6).Value = row.TeacherName ?? string.Empty;
            detail.Cell(line, 7).Value = row.DefaultTeacherName ?? string.Empty;
            detail.Cell(line, 8).Value = row.IsSubstitute ? "是" : string.Empty;
            detail.Cell(line, 9).Value = row.Source == LessonLedgerSource.Profile ? "档案推算" : "教室端";
            line++;
        }
        foreach (var sheet in new[] { summary, detail })
        {
            sheet.Row(1).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(1, Math.Min(line, 200));
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private sealed class Tally
    {
        public int Total;
        public int Substitute;
        public int Substituted;
        public readonly Dictionary<Guid, (string Name, int Total)> Classes = [];
    }

    private async Task<List<LessonLedgerEntry>> LoadAsync(
        IReadOnlyList<Guid>? classScope, DateOnly from, DateOnly to, Guid? classId, bool includeProfile, CancellationToken ct)
    {
        if (to < from) throw new ArgumentException("结束日期不能早于开始日期。");
        if (to.DayNumber - from.DayNumber > MaxRangeDays) throw new ArgumentException($"统计区间不能超过 {MaxRangeDays} 天。");
        var fromKey = ClassSchedulePeriods.DateKey(from);
        var toKey = ClassSchedulePeriods.DateKey(to);
        var query = db.LessonLedgerEntries.AsNoTracking()
            .Where(x => string.Compare(x.Date, fromKey) >= 0 && string.Compare(x.Date, toKey) <= 0);
        if (scopeFilter(classScope) is { } allowed) query = query.Where(x => allowed.Contains(x.ClassId));
        if (classId is { } id) query = query.Where(x => x.ClassId == id);
        if (!includeProfile) query = query.Where(x => x.Source == LessonLedgerSource.ClassIsland);
        return await query.ToListAsync(ct);

        static List<Guid>? scopeFilter(IReadOnlyList<Guid>? scope) => scope?.ToList();
    }
}
