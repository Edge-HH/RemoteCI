using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>管理员可选的课表定时拉取周期，枚举值直接对应持久化的分钟数。</summary>
public enum SchedulePullInterval
{
    Disabled = 0,
    FifteenMinutes = 15,
    Hourly = 60,
    SixHours = 360,
    Daily = 1440,
}

public sealed class SchedulePullSettings(AppDbContext db)
{
    public async Task<SchedulePullInterval> GetIntervalAsync(CancellationToken ct = default)
    {
        var minutes = await db.SystemMetadata.AsNoTracking()
            .Where(metadata => metadata.Id == 1)
            .Select(metadata => metadata.SchedulePullIntervalMinutes)
            .SingleAsync(ct);
        return Enum.IsDefined(typeof(SchedulePullInterval), minutes)
            ? (SchedulePullInterval)minutes
            : SchedulePullInterval.Disabled;
    }

    public async Task SetIntervalAsync(SchedulePullInterval interval, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(interval)) throw new ArgumentOutOfRangeException(nameof(interval));
        var metadata = await db.SystemMetadata.SingleAsync(metadata => metadata.Id == 1, ct);
        metadata.SchedulePullIntervalMinutes = (int)interval;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>记录上次定时拉取尝试，确保周期按“尝试”计算且插件离线时不会每分钟重试。</summary>
internal sealed class SchedulePullCadence(DateTimeOffset startedAt)
{
    private DateTimeOffset _lastAttemptAt = startedAt;

    public bool IsDue(SchedulePullInterval interval, DateTimeOffset now) =>
        interval != SchedulePullInterval.Disabled && now - _lastAttemptAt >= TimeSpan.FromMinutes((int)interval);

    public void MarkAttempt(DateTimeOffset now) => _lastAttemptAt = now;
}
