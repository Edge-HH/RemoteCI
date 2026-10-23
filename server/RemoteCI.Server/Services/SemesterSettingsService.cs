using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 全局学期设置：教学周从学期开始日期所在周的周一算起，供班级档案推算课表的多周轮换和教师自定义课程的单双周使用；
/// 开启同步时下发给支持 <see cref="RemoteCiCapabilities.Semester"/> 的插件，写入 ClassIsland 单周起始日期。
/// </summary>
public sealed class SemesterSettingsService(AppDbContext db, PeerRegistry peers, TimeProvider time)
{
    public async Task<SemesterSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => new { x.SemesterStartDate, x.SemesterSyncToClassrooms }).SingleAsync(ct);
        return new SemesterSettings
        {
            StartDate = row.SemesterStartDate is { } date ? SemesterWeeks.Format(date) : null,
            SyncToClassrooms = row.SemesterSyncToClassrooms,
        };
    }

    public async Task<DateOnly?> GetStartAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1).Select(x => x.SemesterStartDate).SingleAsync(ct);

    public async Task<SemesterSettingsView> GetViewAsync(CancellationToken ct = default)
    {
        var settings = await GetAsync(ct);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        return new SemesterSettingsView
        {
            StartDate = settings.StartDate, SyncToClassrooms = settings.SyncToClassrooms,
            CurrentWeek = SemesterWeeks.Parse(settings.StartDate) is { } start ? SemesterWeeks.TeachingWeek(start, today) : null,
        };
    }

    public async Task<SemesterSettings> SetAsync(SemesterSettings settings, CancellationToken ct = default)
    {
        DateOnly? start = null;
        if (!string.IsNullOrWhiteSpace(settings.StartDate))
            start = SemesterWeeks.Parse(settings.StartDate)
                ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "学期开始日期格式应为 yyyy-MM-dd");
        var row = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        row.SemesterStartDate = start;
        row.SemesterSyncToClassrooms = settings.SyncToClassrooms;
        await db.SaveChangesAsync(ct);
        if (BuildEnvelope(row) is { } envelope)
            await peers.BroadcastToPluginsWithCapabilityAsync(RemoteCiCapabilities.Semester, envelope, ct);
        return await GetAsync(ct);
    }

    /// <summary>插件上报能力后补发；未设置学期或关闭同步时不发送，教室端保持自己的设置。</summary>
    public async Task SendToConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        if (BuildEnvelope(row) is { } envelope) await peers.SendToPluginConnectionAsync(connectionId, envelope, ct);
    }

    private static Envelope? BuildEnvelope(SystemMetadata row) =>
        row.SemesterSyncToClassrooms && row.SemesterStartDate is { } start
            ? Envelope.Semester(new SemesterSync { WeekOneMonday = SemesterWeeks.Format(SemesterWeeks.Monday(start)) })
            : null;
}
