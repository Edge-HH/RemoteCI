using System.Collections.Concurrent;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>单节课临时任课老师覆盖的内存副本（数据库 LessonTeacherOverrides 的镜像）。</summary>
public sealed record LessonOverride(Guid ClassId, string Date, int Index, string TeacherName, string ExpectedSubject);

/// <summary>
/// 换课申请生效后“某班某日某节由谁上”的临时覆盖。ClassIsland 档案只有“班级学科→老师”，
/// 替换为申请人任教的学科、或跨班互换时，该节课的实际老师与学科默认老师不一致，
/// 这里把覆盖叠加到服务端读取的课表上，使个人日程、手机/手表与 WebUI 显示正确的老师。
/// 课位学科已不再是 ExpectedSubject（之后又被换走）时覆盖自动失效。
/// </summary>
public sealed class LessonOverrideTable
{
    private readonly ConcurrentDictionary<(Guid ClassId, string Date, int Index), LessonOverride> _items = new();

    public bool IsEmpty => _items.IsEmpty;

    public void Set(LessonOverride value) => _items[(value.ClassId, value.Date, value.Index)] = value;

    public void Remove(Guid classId, string date, int index) => _items.TryRemove((classId, date, index), out _);

    public void ReplaceAll(IEnumerable<LessonOverride> values)
    {
        _items.Clear();
        foreach (var value in values) Set(value);
    }

    public LessonOverride? Find(Guid classId, string date, int index) =>
        _items.TryGetValue((classId, date, index), out var value) ? value : null;

    /// <summary>返回叠加覆盖后的课表副本；没有命中的覆盖时原样返回同一实例，避免无谓复制。</summary>
    public ScheduleBundle Apply(Guid classId, ScheduleBundle bundle)
    {
        if (_items.IsEmpty || !_items.Keys.Any(x => x.ClassId == classId)) return bundle;
        var changed = false;
        var days = new List<ScheduleDay>(bundle.Days.Count);
        foreach (var day in bundle.Days)
        {
            List<CourseEntry>? courses = null;
            for (var i = 0; i < day.Courses.Count; i++)
            {
                var course = day.Courses[i];
                if (Find(classId, day.Date, course.Index) is not { } value ||
                    !string.Equals(course.Subject, value.ExpectedSubject, StringComparison.Ordinal) ||
                    string.Equals(course.Teacher, value.TeacherName, StringComparison.Ordinal))
                    continue;
                courses ??= [.. day.Courses];
                courses[i] = new CourseEntry
                {
                    Index = course.Index,
                    Label = course.Label,
                    SubjectId = course.SubjectId,
                    Subject = course.Subject,
                    StartTime = course.StartTime,
                    EndTime = course.EndTime,
                    Teacher = value.TeacherName,
                    Enabled = course.Enabled,
                };
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
        };
    }
}
