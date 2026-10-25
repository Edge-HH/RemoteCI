using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>换课后的一节课：班级、课位、时间与新的科目和任课老师。</summary>
public sealed record PlannedLesson(
    Guid ClassId,
    string ClassName,
    string Date,
    int Index,
    string Label,
    string? StartTime,
    string? EndTime,
    string Subject,
    string? Teacher);

/// <summary>
/// 提交或审批换课前的冲突检查：老师撞课（按真实时间，需要确认，见 docs/adr/0002-teacher-clash-is-warning.md）、
/// 同一节课还有其他待审批申请（需要确认，这次生效后它们会失效），以及课位当天已调整过（仅提示）。
/// 生效课表取服务端叠加调课后的课表；插件从未同步过的班级按档案推算，也参与撞课检查。
/// </summary>
public sealed class ScheduleConflictService(
    AppDbContext db,
    IStateStore state,
    ScheduleAdjustmentOverlay overlay,
    ScheduleViewService scheduleViews)
{
    public async Task<IReadOnlyList<SwapConflict>> CheckAsync(
        IReadOnlyList<PlannedLesson> lessons, Guid? excludeRequestId, CancellationToken ct = default)
    {
        var conflicts = new List<SwapConflict>();
        var classes = await db.Classrooms.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var names = classes.ToDictionary(x => x.Id, x => x.Name);

        var schedules = new Dictionary<Guid, ScheduleBundle>();
        foreach (var classroom in classes)
            if (state.GetLatestSchedule(classroom.Id) is { } bundle) schedules[classroom.Id] = bundle;
        foreach (var (classId, view) in await scheduleViews.BuildFromProfilesAsync(
                     classes.Select(x => x.Id).Where(x => !schedules.ContainsKey(x)).ToList(), ct: ct))
            schedules[classId] = view.Bundle;

        var proposed = lessons.Select(x => new ProposedLesson(x.ClassId, x.Date, x.Index, x.Teacher, x.StartTime, x.EndTime)).ToList();
        foreach (var clash in TeacherClashDetector.Find(schedules, proposed))
        {
            var target = lessons.First(x => x.ClassId == clash.Proposed.ClassId && x.Date == clash.Proposed.Date && x.Index == clash.Proposed.Index);
            var className = names.GetValueOrDefault(clash.ClassId, "其他班级");
            conflicts.Add(new SwapConflict
            {
                Kind = SwapConflictKinds.TeacherClash,
                RequiresAcknowledgement = true,
                Message = $"{clash.Teacher} 换到 {target.ClassName} {ShortDate(target.Date)} {target.Label}（{target.StartTime}-{target.EndTime}）后，" +
                          $"与 {className} {clash.Course.Label} {clash.Course.Subject}（{clash.Course.StartTime}-{clash.Course.EndTime}）时间重叠",
                ClassId = clash.ClassId,
                ClassName = className,
                Date = clash.Date,
                Index = clash.Course.Index,
                Label = clash.Course.Label,
                Subject = clash.Course.Subject,
                Teacher = clash.Teacher,
                StartTime = clash.Course.StartTime,
                EndTime = clash.Course.EndTime,
            });
        }

        var pending = await db.ScheduleSwapRequests.AsNoTracking()
            .Where(x => x.Status == SwapRequestStatus.Pending && (excludeRequestId == null || x.Id != excludeRequestId))
            .ToListAsync(ct);
        foreach (var lesson in lessons)
        {
            var others = pending.Where(x => Touches(x, lesson.ClassId, lesson.Date, lesson.Index)).ToList();
            if (others.Count > 0)
                conflicts.Add(new SwapConflict
                {
                    Kind = SwapConflictKinds.SlotPending,
                    RequiresAcknowledgement = true,
                    Message = $"{lesson.ClassName} {ShortDate(lesson.Date)} {lesson.Label} 还有 {others.Count} 份待审批的换课申请，这次换课生效后它们会失效",
                    ClassId = lesson.ClassId,
                    ClassName = lesson.ClassName,
                    Date = lesson.Date,
                    Index = lesson.Index,
                    Label = lesson.Label,
                    SwapRequestId = others[0].Id,
                });
            if (overlay.Find(lesson.ClassId, lesson.Date, lesson.Index) is { } adjusted)
                conflicts.Add(new SwapConflict
                {
                    Kind = SwapConflictKinds.SlotAdjusted,
                    RequiresAcknowledgement = false,
                    Message = $"{lesson.ClassName} {ShortDate(lesson.Date)} {lesson.Label} 当天已调整为 {adjusted.ToSubject}",
                    ClassId = lesson.ClassId,
                    ClassName = lesson.ClassName,
                    Date = lesson.Date,
                    Index = lesson.Index,
                    Label = lesson.Label,
                    Subject = adjusted.ToSubject,
                });
        }
        return conflicts;
    }

    /// <summary>申请是否涉及该课位（目标课或要换走的课）。</summary>
    public static bool Touches(ScheduleSwapRequest row, Guid classId, string date, int index) =>
        (row.TargetClassId == classId && row.TargetDate == date && row.TargetIndex == index) ||
        (row.SourceClassId == classId && row.SourceDate == date && row.SourceIndex == index);

    private static string ShortDate(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("MM-dd", CultureInfo.InvariantCulture)
            : date;
}
