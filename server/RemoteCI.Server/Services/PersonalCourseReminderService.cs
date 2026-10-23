using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed class PersonalCourseReminderService(AppDbContext db, UserNotificationService notifications, SemesterSettingsService semester)
{
    public async Task SendDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // 提醒标记与通知在同一事务保存：服务端重启后不会重复生成。标记记录那次课的开始时刻，每周的同一节课分别提醒。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var semesterStart = await semester.GetStartAsync(ct);
        var rows = await db.PersonalCourses.Where(x => x.User.Enabled && (x.ReminderMinutes != null || x.NotifyAtStart)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var today = PersonalScheduleService.LocalDate(row, now);
            // 提前提醒最长 7 天，向后多看一天即可覆盖。
            var occurrences = PersonalScheduleService.Occurrences(row, today.AddDays(-1), today.AddDays(8), semesterStart).ToList();
            foreach (var occurrence in occurrences)
            {
                var stamp = occurrence.StartsAt.ToUnixTimeMilliseconds();
                var starting = row.NotifyAtStart && row.StartSentFor != stamp &&
                    occurrence.StartsAt <= now && now - occurrence.StartsAt <= TimeSpan.FromMinutes(5);
                var upcoming = row.ReminderMinutes is { } minutes && row.UpcomingSentFor != stamp &&
                    now >= occurrence.StartsAt.AddMinutes(-minutes) && now < occurrence.StartsAt;
                if (!starting && !upcoming) continue;
                if (starting) row.StartSentFor = stamp;
                if (upcoming) row.UpcomingSentFor = stamp;
                var where = string.IsNullOrEmpty(row.Location) ? "" : $" · {row.Location}";
                var local = occurrence.StartsAt;
                await notifications.PublishAsync([row.UserId], starting ? "personal_course_start" : "personal_course_upcoming",
                    starting ? $"上课提醒 · {row.Subject}" : $"即将上课 · {row.Subject}",
                    $"{local:yyyy-MM-dd HH:mm}–{occurrence.EndsAt:HH:mm}{where}", null, ct,
                    expiresAt: starting ? occurrence.EndsAt : occurrence.StartsAt);
                break;
            }
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
