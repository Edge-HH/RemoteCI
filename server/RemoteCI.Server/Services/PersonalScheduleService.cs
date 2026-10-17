using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>所有查询及写入都以认证账号的 UserId 为边界，不按姓名或班级共享。</summary>
public sealed class PersonalScheduleService(AppDbContext db, TeacherBindingService teachers)
{
    public const string Source = "personal";

    public async Task<List<PersonalCourseView>> ListAsync(Guid userId, CancellationToken ct = default) =>
        (await db.PersonalCourses.AsNoTracking().Where(x => x.UserId == userId)
            .OrderBy(x => x.StartsAtUnixMs).ToListAsync(ct)).Select(ToView).ToList();

    public async Task<PersonalCourseView> SaveAsync(Guid userId, Guid? id, PersonalCourseRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Trim().Length > 100 ||
            (request.Location?.Trim().Length ?? 0) > 200)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "课程名称需为 1–100 字，地点最多 200 字");
        if (request.StartsAt == default || request.EndsAt <= request.StartsAt ||
            request.EndsAt - request.StartsAt > TimeSpan.FromDays(1) ||
            request.ReminderMinutes is < 1 or > 10080)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请设置有效的起止时间（最长 24 小时），提前提醒为 1–10080 分钟或 null（关闭）");
        var row = id is null ? new PersonalCourse { Id = Guid.NewGuid(), UserId = userId } :
            await db.PersonalCourses.SingleOrDefaultAsync(x => x.UserId == userId && x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "课程不存在");
        // 修改课程名称或地点不会再次发送已经发出的提醒；调整时间或提醒设置才重新排程。
        if (row.StartsAtUnixMs != request.StartsAt.ToUnixTimeMilliseconds() || row.ReminderMinutes != request.ReminderMinutes)
            row.UpcomingSent = false;
        if (row.StartsAtUnixMs != request.StartsAt.ToUnixTimeMilliseconds() || row.NotifyAtStart != request.NotifyAtStart)
            row.StartSent = false;
        row.Subject = request.Subject.Trim();
        row.Location = request.Location?.Trim() ?? "";
        row.StartsAtUnixMs = request.StartsAt.ToUnixTimeMilliseconds();
        row.EndsAtUnixMs = request.EndsAt.ToUnixTimeMilliseconds();
        row.TimeZoneOffsetMinutes = (int)request.StartsAt.Offset.TotalMinutes;
        row.ReminderMinutes = request.ReminderMinutes;
        row.NotifyAtStart = request.NotifyAtStart;
        if (id is null) db.PersonalCourses.Add(row);
        await db.SaveChangesAsync(ct);
        return ToView(row);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        if (await db.PersonalCourses.Where(x => x.UserId == userId && x.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "课程不存在");
    }

    public async Task<MyScheduleResponse> BuildAsync(Guid userId, string? teacherName, TimeSpan? offset = null, CancellationToken ct = default)
    {
        var result = teacherName is null ? new MyScheduleResponse() : await teachers.BuildMyScheduleAsync(teacherName, offset, ct);
        foreach (var course in await ListAsync(userId, ct))
        {
            var date = course.StartsAt.ToString("yyyy-MM-dd");
            var day = result.Days.FirstOrDefault(x => x.Date == date);
            if (day is null) result.Days.Add(day = new MyScheduleDay { Date = date });
            day.Items.Add(new MyScheduleItem
            {
                ClassName = string.IsNullOrEmpty(course.Location) ? "自定义课程" : course.Location,
                Source = Source,
                TimeZoneOffsetMinutes = (int)course.StartsAt.Offset.TotalMinutes,
                Courses = [ToEntry(course)],
            });
        }
        result.Days = result.Days.OrderBy(x => x.Date, StringComparer.Ordinal).ToList();
        if (result.Days.Count > 0) result.FromDate = result.Days[0].Date;
        return result;
    }

    public async Task<MyNextCourseResponse> NextAsync(Guid userId, string? teacherName, DateTimeOffset now, TimeSpan? offset = null, CancellationToken ct = default)
    {
        var result = teacherName is null ? new MyNextCourseResponse { At = now } : await teachers.BuildMyNextCourseAsync(teacherName, now, offset, ct);
        var slots = (await ListAsync(userId, ct)).Select(course => new MyCourseSlot
        {
            Date = course.StartsAt.ToString("yyyy-MM-dd"),
            ClassName = string.IsNullOrEmpty(course.Location) ? "自定义课程" : course.Location,
            Source = Source, Course = ToEntry(course), StartsAt = course.StartsAt, EndsAt = course.EndsAt,
        }).ToList();
        if (result.Current is not null) slots.Add(result.Current);
        if (result.Next is not null) slots.Add(result.Next);
        result.Current = slots.Where(x => x.StartsAt <= now && now < x.EndsAt).MinBy(x => x.StartsAt);
        result.Next = slots.Where(x => x.StartsAt > now).MinBy(x => x.StartsAt);
        return result;
    }

    internal static PersonalCourseView ToView(PersonalCourse row) => new()
    {
        Id = row.Id, Subject = row.Subject, Location = row.Location,
        StartsAt = DateTimeOffset.FromUnixTimeMilliseconds(row.StartsAtUnixMs).ToOffset(TimeSpan.FromMinutes(row.TimeZoneOffsetMinutes)),
        EndsAt = DateTimeOffset.FromUnixTimeMilliseconds(row.EndsAtUnixMs).ToOffset(TimeSpan.FromMinutes(row.TimeZoneOffsetMinutes)),
        ReminderMinutes = row.ReminderMinutes, NotifyAtStart = row.NotifyAtStart,
    };

    private static CourseEntry ToEntry(PersonalCourseView course) => new()
    {
        PersonalCourseId = course.Id, Label = "自定义", Subject = course.Subject, Enabled = true,
        StartTime = course.StartsAt.ToString("HH:mm"), EndTime = course.EndsAt.ToString("HH:mm"),
        StartsAt = course.StartsAt, EndsAt = course.EndsAt,
        ReminderMinutes = course.ReminderMinutes, NotifyAtStart = course.NotifyAtStart,
    };
}
