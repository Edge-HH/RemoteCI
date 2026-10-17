using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public enum ScheduleViewSource
{
    /// <summary>插件推送的课表；插件离线时为最后一次在线同步的数据。</summary>
    Plugin,
    /// <summary>插件从未同步过课表，由服务端班级档案推算。</summary>
    Profile,
}

/// <summary>课表页展示用的课表：<paramref name="ProfileName"/> 仅在按档案推算时有值。</summary>
public sealed record ScheduleView(ScheduleBundle Bundle, ScheduleViewSource Source, string? ProfileName = null, bool UsesWeekRotation = false);

/// <summary>
/// 课表页、GET /api/schedule 与“我的日程”的课表来源：插件同步过课表的班级始终使用最后一次同步的数据
/// （插件离线时也是，含换课、临时层等教室端实际状态）；插件从未同步过的班级，改用该班当前设置的服务端班级档案
/// 推算未来七日课表。档案推算的课表只用于展示，不参与老师任教班级授权、换课等依赖教室端实际课表的功能。
/// 推算结果不写入状态缓存（<see cref="IStateStore.GetLatestSchedule"/> 仍只表示插件课表），手机/手表经
/// <see cref="PublishProfileSchedulesAsync"/> 与连接时的初始推送收到它（bundle.source = "profile"）。
/// </summary>
public sealed class ScheduleViewService(
    AppDbContext db, IStateStore state, HolidayCalendarService holidays, PeerRegistry peers, ILogger<ScheduleViewService> logger)
{
    public const int Days = 7;

    public async Task<ScheduleView?> GetAsync(Guid classId, CancellationToken ct = default)
    {
        if (state.GetLatestSchedule(classId) is { } cached) return new ScheduleView(cached, ScheduleViewSource.Plugin);
        return (await BuildFromProfilesAsync([classId], ct: ct)).GetValueOrDefault(classId);
    }

    /// <summary>
    /// 把指定班级按档案推算的课表推送给覆盖这些班级的在线手机/手表。插件从未同步过课表的班级没有插件推送，
    /// 班级档案保存后由此刷新；已有插件课表或没有档案的班级跳过。
    /// </summary>
    public async Task PublishProfileSchedulesAsync(IReadOnlyCollection<Guid> classIds, CancellationToken ct = default)
    {
        foreach (var (classId, view) in await BuildFromProfilesAsync(classIds, ct: ct))
            await peers.SendScheduleToWatchesAsync(classId, view.Bundle, ct);
    }

    /// <summary>
    /// 为指定班级中插件从未同步过课表、但保存了班级档案的班级推算课表；已有插件课表或没有档案的班级不在结果中。
    /// 调休日历只读取一次，供“我的日程”一次性汇总多个班级。<paramref name="fallbackOffset"/> 是没有任何班级快照时
    /// 推算“今天”所用的时区（例如手机上报的本机时区），见 <see cref="ClassClock"/>。
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ScheduleView>> BuildFromProfilesAsync(
        IReadOnlyCollection<Guid> classIds, TimeSpan? fallbackOffset = null, CancellationToken ct = default)
    {
        var missing = classIds.Where(id => state.GetLatestSchedule(id) is null).Distinct().ToList();
        var result = new Dictionary<Guid, ScheduleView>();
        if (missing.Count == 0) return result;
        // 每个班级至多一份独立档案（ProfileLibraryService 保证），即该班当前设置的档案。
        var profiles = await db.StoredProfiles.AsNoTracking()
            .Where(x => x.ClassId != null && missing.Contains(x.ClassId.Value))
            .Select(x => new { ClassId = x.ClassId!.Value, x.Name, x.ProfileJson })
            .ToListAsync(ct);
        if (profiles.Count == 0) return result;
        var calendar = await holidays.BuildCalendarAsync(ct);
        var holidayDays = calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal)
            : new Dictionary<string, HolidayCalendarDay>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            try
            {
                var built = ProfileDocument.BuildSchedule(profile.ProfileJson, ClassClock.Today(state, profile.ClassId, fallback: fallbackOffset), Days,
                    date => holidayDays.GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                built.Bundle.ClassId = profile.ClassId;
                result[profile.ClassId] = new ScheduleView(built.Bundle, ScheduleViewSource.Profile, profile.Name, built.UsesWeekRotation);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "班级 {ClassId} 的档案无法推算课表", profile.ClassId);
            }
        }
        return result;
    }
}
