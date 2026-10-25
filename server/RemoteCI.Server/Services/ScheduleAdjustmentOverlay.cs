using System.Collections.Concurrent;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>某课位当前生效（叠放最上层）的调课记录在内存中的副本。</summary>
public sealed record AdjustmentOverlayEntry(
    Guid Id,
    Guid ClassId,
    string Date,
    int Index,
    Guid FromSubjectId,
    Guid ToSubjectId,
    string ToSubject,
    string? TeacherName);

/// <summary>
/// 把服务端调课记录叠加到插件上报的课表上（数据库 ScheduleAdjustments 有效记录的内存镜像），
/// 使 WebUI、手机/手表、个人日程与换课检查看到同一份生效课表：
/// 课位已是新科目时只补上临时任课老师；仍是原科目（教室端离线或尚未回执）时按记录显示并标记待生效；
/// 既不是原科目也不是新科目时以教室端实况为准。按科目 ID 匹配，时间表变化后不会把老师套到别的节次上。
/// </summary>
public sealed class ScheduleAdjustmentOverlay
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<(string Date, int Index), AdjustmentOverlayEntry>> _classes = new();

    public bool IsEmpty => _classes.IsEmpty;

    public void ReplaceAll(IEnumerable<AdjustmentOverlayEntry> entries)
    {
        var grouped = entries.GroupBy(x => x.ClassId).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var classId in _classes.Keys.Except(grouped.Keys).ToList()) _classes.TryRemove(classId, out _);
        foreach (var (classId, items) in grouped) ReplaceClass(classId, items);
    }

    public void ReplaceClass(Guid classId, IEnumerable<AdjustmentOverlayEntry> entries)
    {
        var map = entries.ToDictionary(x => (x.Date, x.Index));
        if (map.Count == 0) _classes.TryRemove(classId, out _);
        else _classes[classId] = map;
    }

    public AdjustmentOverlayEntry? Find(Guid classId, string date, int index) =>
        _classes.TryGetValue(classId, out var map) && map.TryGetValue((date, index), out var entry) ? entry : null;

    /// <summary>返回叠加调课后的课表副本；该班没有命中的记录时原样返回同一实例，避免无谓复制。</summary>
    public ScheduleBundle Apply(Guid classId, ScheduleBundle bundle)
    {
        if (!_classes.TryGetValue(classId, out var map) || map.Count == 0) return bundle;
        var changed = false;
        var days = new List<ScheduleDay>(bundle.Days.Count);
        foreach (var day in bundle.Days)
        {
            List<CourseEntry>? courses = null;
            for (var i = 0; i < day.Courses.Count; i++)
            {
                var course = day.Courses[i];
                if (!map.TryGetValue((day.Date, course.Index), out var entry)) continue;
                CourseEntry? replaced = null;
                if (course.SubjectId == entry.ToSubjectId)
                {
                    if ((entry.TeacherName is not null && !string.Equals(course.Teacher, entry.TeacherName, StringComparison.Ordinal)) ||
                        course.AdjustmentId is null)
                        replaced = Copy(course, entry.ToSubjectId, course.Subject, entry.TeacherName ?? course.Teacher, entry, pending: null);
                }
                else if (course.SubjectId == entry.FromSubjectId)
                {
                    var teacher = entry.TeacherName ?? bundle.Subjects.FirstOrDefault(x => x.Id == entry.ToSubjectId)?.Teacher;
                    replaced = Copy(course, entry.ToSubjectId, entry.ToSubject, teacher, entry, pending: true);
                }
                if (replaced is null) continue;
                courses ??= [.. day.Courses];
                courses[i] = replaced;
            }
            if (courses is null)
            {
                days.Add(day);
                continue;
            }
            changed = true;
            days.Add(new ScheduleDay
            {
                Date = day.Date,
                Revision = day.Revision,
                ClassPlanName = day.ClassPlanName,
                Enabled = day.Enabled,
                Courses = courses,
                DayKind = day.DayKind,
                HolidayName = day.HolidayName,
            });
        }
        if (!changed) return bundle;
        return new ScheduleBundle
        {
            FromDate = bundle.FromDate,
            GeneratedAt = bundle.GeneratedAt,
            ClassId = bundle.ClassId,
            Days = days,
            Subjects = bundle.Subjects,
            Source = bundle.Source,
        };
    }

    private static CourseEntry Copy(CourseEntry course, Guid subjectId, string subject, string? teacher,
        AdjustmentOverlayEntry entry, bool? pending) => new()
    {
        Index = course.Index,
        Label = course.Label,
        SubjectId = subjectId,
        Subject = subject,
        StartTime = course.StartTime,
        EndTime = course.EndTime,
        Teacher = teacher,
        Enabled = course.Enabled,
        BaseSubjectId = course.BaseSubjectId ?? (subjectId == entry.FromSubjectId ? null : entry.FromSubjectId),
        AdjustmentId = course.AdjustmentId ?? entry.Id,
        AdjustmentPending = pending,
    };
}
