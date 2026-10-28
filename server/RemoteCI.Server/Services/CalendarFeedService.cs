using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>Read-only, revocable feeds. Authentication is re-evaluated on every fetch, independent of browser cookies.</summary>
public sealed class CalendarFeedService(AppDbContext db, ClassAccessService access, PersonalScheduleService personal,
    ScheduleViewService schedules, IStateStore state, TimeProvider time)
{
    // 每个 (用户, 班级) 最多一条有效订阅：同一日历的创建与拉取互斥，不同日历可并发。
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim Stripe(Guid userId, Guid? classId) => Stripes[(uint)HashCode.Combine(userId, classId) % Stripes.Length];
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<(CalendarSubscription Subscription, string Token)> CreateAsync(AppUser user, Guid? classId, int? reminder, int offset, CancellationToken ct)
    {
        var gate = Stripe(user.Id, classId);
        await gate.WaitAsync(ct);
        try { return await CreateCoreAsync(user, classId, reminder, offset, ct); }
        finally { gate.Release(); }
    }

    private async Task<(CalendarSubscription Subscription, string Token)> CreateCoreAsync(AppUser user, Guid? classId, int? reminder, int offset, CancellationToken ct)
    {
        if (reminder is not (null or 0 or 5 or 10 or 15 or 30) || ClassClock.Valid(offset) is null)
            throw new ArgumentException("请选择有效的提醒时间和时区");
        if (!await AllowedAsync(user, classId, ct)) throw new UnauthorizedAccessException();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Re-creating the same calendar rotates its URL; independent class and personal feeds remain intact.
        await db.CalendarSubscriptions.Where(x => x.UserId == user.Id && x.ClassId == classId).ExecuteDeleteAsync(ct);
        var token = WebPushSender.Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new CalendarSubscription { UserId = user.Id, ClassId = classId, TokenHash = Hash(token), ReminderMinutes = reminder, OffsetMinutes = offset };
        db.CalendarSubscriptions.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (row, token);
    }

    public async Task<string?> ReadAsync(string token, CancellationToken ct)
    {
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        var row = await db.CalendarSubscriptions.Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == Hash(token), ct);
        if (row is null || row.Revoked) return null;
        var gate = Stripe(row.UserId, row.ClassId);
        await gate.WaitAsync(ct);
        try
        {
            // 等锁期间同一日历可能已被重新创建（轮换链接）或撤销。
            if (!await db.CalendarSubscriptions.AnyAsync(x => x.Id == row.Id && !x.Revoked, ct)) return null;
            return await ReadCoreAsync(row, ct);
        }
        // 恢复配置等批量删除与本次写入并发：订阅已不存在，按失效处理。
        catch (DbUpdateException) { return null; }
        finally { gate.Release(); }
    }

    private async Task<string?> ReadCoreAsync(CalendarSubscription row, CancellationToken ct)
    {
        if (!row.User.Enabled || !await AllowedAsync(row.User, row.ClassId, ct)) return null;
        var now = time.GetUtcNow();
        var desired = new Dictionary<string, (string Content, long EndsAt)>();
        var name = "RemoteCI 我的日程";
        if (row.ClassId is { } classId)
        {
            var classroom = await db.Classrooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == classId, ct);
            name = "RemoteCI " + classroom?.Name;
            var view = await schedules.GetAsync(classId, ct);
            if (view is not null)
                // 与课表页一致只放 7 天；插件多上报的日期只供换课选择。
                foreach (var day in view.Bundle.Days.Take(ScheduleBundle.DisplayDays).Where(x => x.Enabled))
                    foreach (var course in day.Courses.Where(x => x.Enabled))
                        Add(course, day.Date, classId, classroom?.Name ?? "", view.Bundle.Source,
                            ClassClock.Offset(state, classId, now, TimeSpan.FromMinutes(row.OffsetMinutes)));
        }
        else
        {
            var schedule = await personal.BuildAsync(row.UserId,
                AccountRole.HasPersonalSchedule(row.User.RoleDefinitionId) ? row.User.DisplayName : null,
                TimeSpan.FromMinutes(row.OffsetMinutes), ct);
            foreach (var day in schedule.Days)
                foreach (var item in day.Items)
                    foreach (var course in item.Courses.Where(x => x.Enabled))
                        Add(course, day.Date, item.ClassId, item.ClassName, item.Source,
                            ClassClock.Valid(item.TimeZoneOffsetMinutes) ?? TimeSpan.FromMinutes(row.OffsetMinutes));
        }
        var versions = await db.CalendarEventVersions.Where(x => x.SubscriptionId == row.Id).ToListAsync(ct);
        foreach (var (uid, value) in desired)
        {
            var version = versions.FirstOrDefault(x => x.Uid == uid);
            if (version is null)
            {
                version = new CalendarEventVersion { SubscriptionId = row.Id, Uid = uid };
                db.CalendarEventVersions.Add(version); versions.Add(version);
            }
            var fingerprint = Hash(value.Content);
            if (version.Fingerprint != fingerprint || version.Cancelled)
            {
                version.Sequence++;
                version.Fingerprint = fingerprint; version.Content = value.Content;
                version.UpdatedAt = now.ToUnixTimeMilliseconds(); version.EndsAt = value.EndsAt; version.Cancelled = false;
            }
        }
        foreach (var version in versions.Where(x => !desired.ContainsKey(x.Uid) && !x.Cancelled && x.EndsAt > now.ToUnixTimeMilliseconds()))
        { version.Cancelled = true; version.Sequence++; version.UpdatedAt = now.ToUnixTimeMilliseconds(); }
        // Retain recent completed events, and cancellation tombstones long enough for sleeping clients to refresh.
        var cutoff = now.AddDays(-30).ToUnixTimeMilliseconds();
        db.CalendarEventVersions.RemoveRange(versions.Where(x => x.EndsAt < cutoff));
        await db.SaveChangesAsync(ct);
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//RemoteCI//Schedule//ZH-CN", "CALSCALE:GREGORIAN", "X-WR-CALNAME:" + Escape(name), "REFRESH-INTERVAL;VALUE=DURATION:PT1H", "X-PUBLISHED-TTL:PT1H" };
        foreach (var version in versions.Where(x => x.EndsAt >= cutoff).OrderBy(x => x.Uid, StringComparer.Ordinal))
        {
            lines.Add("BEGIN:VEVENT"); lines.Add("UID:" + version.Uid);
            lines.Add("DTSTAMP:" + Utc(DateTimeOffset.FromUnixTimeMilliseconds(version.UpdatedAt)));
            lines.Add("LAST-MODIFIED:" + Utc(DateTimeOffset.FromUnixTimeMilliseconds(version.UpdatedAt)));
            lines.Add("SEQUENCE:" + version.Sequence.ToString(CultureInfo.InvariantCulture));
            lines.AddRange(version.Content.Split('\n'));
            if (version.Cancelled) lines.Add("STATUS:CANCELLED");
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";

        void Add(CourseEntry course, string date, Guid classId, string location, string? source, TimeSpan offset)
        {
            var start = course.StartsAt;
            var end = course.EndsAt;
            if (start is null || end is null)
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
                    !TimeOnly.TryParse(course.StartTime, CultureInfo.InvariantCulture, out var s) ||
                    !TimeOnly.TryParse(course.EndTime, CultureInfo.InvariantCulture, out var e)) return;
                start = new DateTimeOffset(d.ToDateTime(s), offset);
                end = new DateTimeOffset(d.ToDateTime(e), offset);
                if (end < start) end = end.Value.AddDays(1);
            }
            if (end <= start || end < now.AddDays(-30)) return;
            // 自定义课程每周重复，按课程与日期区分每一次。
            var uid = course.PersonalCourseId is { } id ? $"personal-{id:N}-{date}@remoteci" : $"class-{classId:N}-{date}-{course.Index}@remoteci";
            var content = new List<string> { "DTSTART:" + Utc(start.Value), "DTEND:" + Utc(end.Value), "SUMMARY:" + Escape(course.Subject),
                "LOCATION:" + Escape(location), "DESCRIPTION:" + Escape(source == ScheduleBundleSources.Profile ? "按班级档案推算，可能与教室端实际课程不同" : "由 RemoteCI 同步；请在 RemoteCI 修改课程") };
            var reminder = course.PersonalCourseId is not null ? course.ReminderMinutes : row.ReminderMinutes;
            if (reminder is { } minutes) Alarm(minutes);
            if (course.PersonalCourseId is not null && course.NotifyAtStart && reminder != 0) Alarm(0);
            desired[uid] = (string.Join('\n', content), end.Value.ToUnixTimeMilliseconds());
            void Alarm(int minutes) => content.AddRange(["BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:" + Escape(course.Subject), "TRIGGER:-PT" + minutes.ToString(CultureInfo.InvariantCulture) + "M", "END:VALARM"]);
        }
    }

    private async Task<bool> AllowedAsync(AppUser user, Guid? classId, CancellationToken ct) => user.Enabled &&
        (classId is null || (await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct)).Any(x => x.Id == classId));
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    public static string Fold(string value)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 75) { result.Append("\r\n "); bytes = 1; }
            result.Append(rune); bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
