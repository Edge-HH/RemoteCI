using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed class PersonalCourseReminderService(AppDbContext db, UserNotificationService notifications)
{
    public async Task SendDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // 提醒标记与通知在同一事务保存：服务端重启后不会重复生成。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var stamp = now.ToUnixTimeMilliseconds();
        var rows = await db.PersonalCourses.Where(x => x.User.Enabled && x.EndsAtUnixMs > stamp &&
            ((!x.UpcomingSent && x.ReminderMinutes != null) || (!x.StartSent && x.NotifyAtStart))).ToListAsync(ct);
        foreach (var row in rows)
        {
            var starting = row.NotifyAtStart && !row.StartSent && row.StartsAtUnixMs <= stamp && stamp - row.StartsAtUnixMs <= 300_000;
            var upcoming = !row.UpcomingSent && row.ReminderMinutes is { } minutes &&
                stamp >= row.StartsAtUnixMs - minutes * 60_000L && stamp < row.StartsAtUnixMs;
            if (!starting && !upcoming) continue;
            if (starting) row.StartSent = true;
            if (upcoming) row.UpcomingSent = true;
            var course = PersonalScheduleService.ToView(row);
            var where = string.IsNullOrEmpty(course.Location) ? "" : $" · {course.Location}";
            await notifications.PublishAsync([row.UserId], starting ? "personal_course_start" : "personal_course_upcoming",
                starting ? $"上课提醒 · {row.Subject}" : $"即将上课 · {row.Subject}",
                $"{course.StartsAt:yyyy-MM-dd HH:mm}–{course.EndsAt:HH:mm}{where}", null, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public sealed class PersonalCourseReminderWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<PersonalCourseReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PersonalCourseReminderService>().SendDueAsync(time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "发送自定义课程提醒失败"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
