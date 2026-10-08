using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed record HolidaySettings(bool Enabled, string? SourceUrlTemplate);

/// <summary>调休功能的持久化：总开关与数据源、补课覆盖、年度数据快照。</summary>
public sealed class HolidaySettingsStore(AppDbContext db)
{
    public const int MaxTemplateLength = 512;

    public async Task<HolidaySettings> GetSettingsAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => new HolidaySettings(x.HolidayCalendarEnabled, x.HolidaySourceUrlTemplate))
            .SingleAsync(ct);

    public async Task SetSettingsAsync(bool enabled, string? template, CancellationToken ct = default)
    {
        var normalized = NormalizeTemplate(template);
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.HolidayCalendarEnabled = enabled;
        metadata.HolidaySourceUrlTemplate = normalized;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>空白视为使用默认源；否则必须是包含 {year} 的 https 地址，避免明文下载被篡改后误关课表。</summary>
    public static string? NormalizeTemplate(string? template)
    {
        var value = template?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > MaxTemplateLength ||
            !value.Contains("{year}", StringComparison.Ordinal) ||
            !Uri.TryCreate(value.Replace("{year}", "2026", StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("数据源地址必须是包含 {year} 的 https 地址");
        return value;
    }

    public async Task<IReadOnlyList<HolidayOverride>> GetOverridesAsync(CancellationToken ct = default) =>
        await db.HolidayMakeupOverrides.AsNoTracking()
            .OrderBy(x => x.Date)
            .Select(x => new HolidayOverride(x.Date, x.FollowWeekday))
            .ToListAsync(ct);

    public async Task SetOverrideAsync(
        DateOnly date, int? followWeekday, Guid? userId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (followWeekday is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(followWeekday), "补课只能安排周一至周五（1-5）的课");
        var row = await db.HolidayMakeupOverrides.FindAsync([date], ct);
        if (row is null)
            db.HolidayMakeupOverrides.Add(row = new HolidayMakeupOverride { Date = date });
        row.FollowWeekday = followWeekday;
        row.UpdatedByUserId = userId;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveOverrideAsync(DateOnly date, CancellationToken ct = default) =>
        await db.HolidayMakeupOverrides.Where(x => x.Date == date).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<HolidayYearSnapshot>> GetSnapshotsAsync(CancellationToken ct = default) =>
        await db.HolidayYearSnapshots.AsNoTracking().OrderBy(x => x.Year).ToListAsync(ct);

    public async Task SaveSnapshotAsync(
        int year, string rawJson, string sourceUrl, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        var row = await db.HolidayYearSnapshots.FindAsync([year], ct);
        if (row is null)
            db.HolidayYearSnapshots.Add(row = new HolidayYearSnapshot { Year = year });
        row.RawJson = rawJson;
        row.SourceUrl = sourceUrl;
        row.FetchedAt = fetchedAt;
        await db.SaveChangesAsync(ct);
    }
}
