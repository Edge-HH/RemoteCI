using System.Globalization;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>换课后某节课的新任课老师与上课时间。</summary>
public sealed record ProposedLesson(Guid ClassId, string Date, int Index, string? Teacher, string? StartTime, string? EndTime);

/// <summary>撞课：新任课老师 <see cref="Teacher"/> 在同一时间还有另一节课。</summary>
public sealed record TeacherClash(ProposedLesson Proposed, string Teacher, Guid ClassId, string Date, CourseEntry Course);

/// <summary>
/// 任课冲突检查（纯函数）：按真实上下课时间判断同一位老师是否同时在两处上课，不比较节次序号
/// （各班时间表可以不同）。本次换课腾出的课位不算冲突；没有时间的课跳过。
/// </summary>
public static class TeacherClashDetector
{
    public static IReadOnlyList<TeacherClash> Find(
        IReadOnlyDictionary<Guid, ScheduleBundle> schedules,
        IReadOnlyList<ProposedLesson> proposed)
    {
        var changed = proposed.Select(x => (x.ClassId, x.Date, x.Index)).ToHashSet();
        var result = new List<TeacherClash>();
        foreach (var lesson in proposed)
        {
            if (!TryRange(lesson.StartTime, lesson.EndTime, out var start, out var end)) continue;
            foreach (var teacher in TeacherBindingService.SplitNames(lesson.Teacher))
            {
                foreach (var (classId, bundle) in schedules)
                {
                    var day = bundle.Days.FirstOrDefault(x => x.Date == lesson.Date);
                    if (day is null || !day.Enabled) continue;
                    foreach (var course in day.Courses)
                    {
                        if (!course.Enabled || changed.Contains((classId, day.Date, course.Index))) continue;
                        if (!TeacherBindingService.Matches(teacher, course.Teacher)) continue;
                        if (!TryRange(course.StartTime, course.EndTime, out var otherStart, out var otherEnd)) continue;
                        if (start < otherEnd && otherStart < end)
                            result.Add(new TeacherClash(lesson, teacher, classId, day.Date, course));
                    }
                }
            }
        }
        return result;
    }

    private static bool TryRange(string? start, string? end, out TimeSpan from, out TimeSpan to)
    {
        to = default;
        return TimeSpan.TryParseExact(start, "hh\\:mm", CultureInfo.InvariantCulture, out from) &&
               TimeSpan.TryParseExact(end, "hh\\:mm", CultureInfo.InvariantCulture, out to) &&
               to > from;
    }
}
