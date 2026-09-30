using System.Security.Cryptography;
using System.Text;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>七日课表读取和修订号计算的唯一入口（经 IScheduleBackend 防腐层，可单元测试）。</summary>
public sealed class ScheduleCatalog(IScheduleBackend backend)
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
                .Select(x => new SubjectEntry { Id = x.Key, Name = x.Value.Name })
                // Ordinal 排序不受运行环境区域设置影响，保证同一课表各端看到的顺序一致。
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public ScheduleDay BuildDay(DateTime date)
    {
        var day = date.Date;
        var plan = backend.GetClassPlan(day, out var planId);
        var result = new ScheduleDay
        {
            Date = day.ToString("yyyy-MM-dd"),
            ClassPlanName = plan?.Name,
            Enabled = plan is not null,
            Courses = plan?.Classes.Select((course, index) => ToCourse(course, index)).ToList() ?? [],
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
            Enabled = course.IsEnabled,
        };
    }

    private static string ComputeRevision(ScheduleDay day, Guid? planId)
    {
        var canonical = new StringBuilder(day.Date).Append('|').Append(planId).Append('|').Append(day.Enabled);
        foreach (var course in day.Courses)
            canonical.Append('|').Append(course.Index).Append(':').Append(course.SubjectId).Append(':').Append(course.Enabled)
                .Append(':').Append(course.StartTime).Append(':').Append(course.EndTime);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
