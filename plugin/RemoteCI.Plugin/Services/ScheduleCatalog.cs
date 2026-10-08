using System.Security.Cryptography;
using System.Text;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>七日课表读取和修订号计算的唯一入口（经 IScheduleBackend 防腐层，可单元测试）。</summary>
public sealed class ScheduleCatalog(IScheduleBackend backend, IHolidayCalendarLookup? holidays = null)
{
    public ScheduleBundle BuildBundle(DateTime? start = null)
    {
        var from = (start ?? DateTime.Today).Date;
        return new ScheduleBundle
        {
            FromDate = from.ToString("yyyy-MM-dd"),
            GeneratedAt = DateTimeOffset.UtcNow,
            Days = Enumerable.Range(0, 7).Select(offset => BuildDay(from.AddDays(offset))).ToList(),
            Subjects = backend.Subjects
                .Where(x => !string.IsNullOrWhiteSpace(x.Value.Name) && x.Value.Name != StateCollector.UnsetSubjectPlaceholder)
                .Select(x => new SubjectEntry { Id = x.Key, Name = x.Value.Name, Teacher = NormalizeTeacher(x.Value.TeacherName) })
                // Ordinal 排序不受运行环境区域设置影响，保证同一课表各端看到的顺序一致。
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public ScheduleDay BuildDay(DateTime date)
    {
        var day = date.Date;
        var plan = backend.GetClassPlan(day, out var planId);
        var holiday = holidays?.Find(day);
        // 放假日宿主课表开关已被关闭，按规则匹配到的课表并不会真的上，因此不再上报课程，也不允许换课。
        var isOff = holiday?.Kind == HolidayDayKinds.Off;
        var result = new ScheduleDay
        {
            Date = day.ToString("yyyy-MM-dd"),
            ClassPlanName = isOff ? null : plan?.Name,
            Enabled = !isOff && plan is not null,
            Courses = isOff ? [] : plan?.Classes.Select((course, index) => ToCourse(course, index)).ToList() ?? [],
            DayKind = holiday?.Kind switch
            {
                HolidayDayKinds.Off => ScheduleDayKinds.Holiday,
                HolidayDayKinds.Makeup => ScheduleDayKinds.Makeup,
                _ => null,
            },
            HolidayName = holiday?.Name,
        };
        result.Revision = ComputeRevision(result, planId);
        return result;
    }

    private CourseEntry ToCourse(ClassInfo course, int index)
    {
        backend.Subjects.TryGetValue(course.SubjectId, out var subject);
        var item = course.CurrentTimeLayoutItem;
        return new CourseEntry
        {
            Index = index,
            Label = $"第 {index + 1} 节",
            SubjectId = course.SubjectId,
            Subject = subject?.Name is not null and not StateCollector.UnsetSubjectPlaceholder ? subject.Name : "未设置",
            StartTime = item == TimeLayoutItem.Empty ? null : item.StartTime.ToString("hh\\:mm"),
            EndTime = item == TimeLayoutItem.Empty ? null : item.EndTime.ToString("hh\\:mm"),
            Teacher = subject is null ? null : NormalizeTeacher(subject.TeacherName),
            Enabled = course.IsEnabled,
        };
    }

    /// <summary>教师名统一去首尾空白，空白视为未设置。</summary>
    internal static string? NormalizeTeacher(string? teacherName)
    {
        var name = teacherName?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string ComputeRevision(ScheduleDay day, Guid? planId)
    {
        var canonical = new StringBuilder(day.Date).Append('|').Append(planId).Append('|').Append(day.Enabled);
        foreach (var course in day.Courses)
            canonical.Append('|').Append(course.Index).Append(':').Append(course.SubjectId).Append(':').Append(course.Enabled)
                .Append(':').Append(course.StartTime).Append(':').Append(course.EndTime);
        // 只在有标记时参与计算，普通日期的修订号与旧版本保持一致。
        if (day.DayKind is not null)
            canonical.Append("|day:").Append(day.DayKind).Append(':').Append(day.HolidayName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
