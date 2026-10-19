using System.Globalization;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>课表中的一节课，起止时刻已按教室端时区换算为绝对时间。</summary>
public sealed record LessonSlot(ScheduleDay Day, CourseEntry Course, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// 把课表里教室端本地钟点（HH:mm）换算成绝对时间，供离线告警的在校时段、课时台账与自动化触发共用。
/// 放假日（Enabled=false）和停用的课程不计入。
/// </summary>
public static class ClassSchedulePeriods
{
    public static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static ScheduleDay? FindDay(ScheduleBundle bundle, DateOnly date)
    {
        var key = DateKey(date);
        return bundle.Days.FirstOrDefault(x => x.Date == key);
    }

    public static IReadOnlyList<LessonSlot> Lessons(ScheduleDay? day, TimeSpan offset)
    {
        if (day is null || !day.Enabled) return [];
        if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return [];
        var lessons = new List<LessonSlot>();
        foreach (var course in day.Courses.Where(x => x.Enabled))
        {
            if (!TimeOnly.TryParse(course.StartTime, CultureInfo.InvariantCulture, out var start) ||
                !TimeOnly.TryParse(course.EndTime, CultureInfo.InvariantCulture, out var end)) continue;
            var startsAt = new DateTimeOffset(date.ToDateTime(start), offset);
            var endsAt = new DateTimeOffset(date.ToDateTime(end), offset);
            // 跨午夜的晚自习：结束钟点早于开始钟点时算到第二天。
            if (endsAt <= startsAt) endsAt = endsAt.AddDays(1);
            lessons.Add(new LessonSlot(day, course, startsAt, endsAt));
        }
        return lessons.OrderBy(x => x.StartsAt).ToList();
    }

    /// <summary>当天在校时段：第一节开始前 <paramref name="lead"/> 至最后一节结束；当天没有课返回 null。</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? SchoolHours(ScheduleDay? day, TimeSpan offset, TimeSpan lead)
    {
        var lessons = Lessons(day, offset);
        if (lessons.Count == 0) return null;
        return (lessons[0].StartsAt - lead, lessons.Max(x => x.EndsAt));
    }
}
