using System.Globalization;
using System.Text.Json;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>调休所需的宿主能力（ClassIsland 服务含 internal 成员，外部程序集无法实现假对象，故经此防腐层）。</summary>
public interface IHolidayHostOperations
{
    /// <summary>宿主 LessonsService 的“启用课表”运行时开关；宿主不支持时为 null。</summary>
    bool? IsClassPlanEnabled { get; set; }
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);
    /// <summary>该日预定课表指向的课表，没有则为 null。</summary>
    Guid? GetOrderedSchedulePlanId(DateTime date);
    Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date);
    /// <summary>仅当该日预定课表仍指向 planId 时删除它及对应的临时课表。</summary>
    void RemoveOrderedSchedule(DateTime date, Guid planId);
    void SaveProfile();
}

public sealed record MakeupRecord(Guid PlanId, int FollowWeekday);

/// <summary>插件为调休上学日建立过的临时课表；落盘以便重启后不重复建立，也能在安排变化时撤掉。</summary>
public sealed class HolidayStateFile(string path)
{
    private readonly Dictionary<string, MakeupRecord> _makeups = Load(path);

    public IReadOnlyDictionary<string, MakeupRecord> Makeups => _makeups;
    public void Set(string date, MakeupRecord record) => _makeups[date] = record;
    public void Remove(string date) => _makeups.Remove(date);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StateDocument { Makeups = _makeups }, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去时本次运行内仍按内存状态工作；最坏情况是重启后把同一天的临时课表视为他人安排而不再处理。
        }
    }

    private static Dictionary<string, MakeupRecord> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonDefaults.Options)?.Makeups ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class StateDocument
    {
        public Dictionary<string, MakeupRecord> Makeups { get; set; } = [];
    }
}

/// <summary>
/// 按调休日历调整 ClassIsland：放假日关闭课表（只恢复自己关掉的），调休上学日建立临时课表（不碰他人安排）。
/// 必须在 UI 线程调用。
/// </summary>
public sealed class HolidayScheduleApplier(
    IHolidayHostOperations host, HolidayStateFile state, ILogger<HolidayScheduleApplier> logger)
{
    public const int MakeupLookaheadDays = 7;
    private bool _disabledByUs;
    private DateTime? _offDayHandled;
    private bool _warnedUnsupported;

    /// <summary>课表内容有变化（建立或撤销了临时课表、切换了开关）时返回 true，调用方据此重新上报课表。</summary>
    public bool Apply(HolidayCalendar calendar, DateTime now)
    {
        var today = now.Date;
        var days = calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First())
            : new Dictionary<string, HolidayCalendarDay>();
        var changed = ApplyOffDay(days, today);
        var stateChanged = CleanupMakeups(days, today, ref changed);
        stateChanged |= CreateMakeups(days, today, ref changed);
        if (stateChanged) state.Save();
        return changed;
    }

    /// <summary>调休上学日所在周（周一为第一天）中对应工作日的日期。</summary>
    public static DateTime SourceDate(DateTime makeupDate, int followWeekday)
    {
        var monday = makeupDate.Date.AddDays(-(((int)makeupDate.DayOfWeek + 6) % 7));
        return monday.AddDays(followWeekday - 1);
    }

    private bool ApplyOffDay(Dictionary<string, HolidayCalendarDay> days, DateTime today)
    {
        var isOff = days.TryGetValue(Key(today), out var day) && day.Kind == HolidayDayKinds.Off;
        if (isOff)
        {
            // 同一天只处理一次：老师当天手动重新打开课表后，插件不再把它关掉。
            if (_offDayHandled == today) return false;
            _offDayHandled = today;
            switch (host.IsClassPlanEnabled)
            {
                case null:
                    if (!_warnedUnsupported) logger.LogWarning("当前 ClassIsland 不支持切换“启用课表”，放假日无法自动关闭课表");
                    _warnedUnsupported = true;
                    return false;
                case true:
                    host.IsClassPlanEnabled = false;
                    _disabledByUs = true;
                    logger.LogInformation("{Date} 为{Name}放假日，已关闭课表", Key(today), day!.Name);
                    return true;
                default:
                    return false;
            }
        }

        _offDayHandled = null;
        if (!_disabledByUs) return false;
        _disabledByUs = false;
        if (host.IsClassPlanEnabled != false) return false;
        host.IsClassPlanEnabled = true;
        logger.LogInformation("放假结束，已恢复课表");
        return true;
    }

    private bool CleanupMakeups(Dictionary<string, HolidayCalendarDay> days, DateTime today, ref bool changed)
    {
        var stateChanged = false;
        foreach (var (key, record) in state.Makeups.ToList())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < today)
            {
                state.Remove(key);
                stateChanged = true;
                continue;
            }
            var desired = days.TryGetValue(key, out var day) && day.Kind == HolidayDayKinds.Makeup ? day.FollowWeekday : null;
            if (desired == record.FollowWeekday) continue;
            if (host.GetOrderedSchedulePlanId(date) == record.PlanId)
            {
                host.RemoveOrderedSchedule(date, record.PlanId);
                host.SaveProfile();
                changed = true;
                logger.LogInformation("{Date} 的调休安排已变化，已撤销之前建立的临时课表", key);
            }
            state.Remove(key);
            stateChanged = true;
        }
        return stateChanged;
    }

    private bool CreateMakeups(Dictionary<string, HolidayCalendarDay> days, DateTime today, ref bool changed)
    {
        var stateChanged = false;
        for (var offset = 0; offset < MakeupLookaheadDays; offset++)
        {
            var date = today.AddDays(offset);
            var key = Key(date);
            if (!days.TryGetValue(key, out var day) || day.Kind != HolidayDayKinds.Makeup || day.FollowWeekday is not { } weekday)
                continue;
            if (state.Makeups.ContainsKey(key) || host.GetOrderedSchedulePlanId(date) is not null)
                continue;
            var source = SourceDate(date, weekday);
            if (host.GetClassPlan(source, out var sourceId) is null || sourceId is null)
            {
                logger.LogWarning("{Date} 调休应上 {Source} 的课，但该日没有可用课表", key, Key(source));
                continue;
            }
            if (host.CreateTempClassPlan(sourceId.Value, date) is not { } overlayId)
            {
                logger.LogWarning("{Date} 无法建立调休临时课表", key);
                continue;
            }
            try
            {
                host.SaveProfile();
            }
            catch (Exception ex)
            {
                host.RemoveOrderedSchedule(date, overlayId);
                logger.LogError(ex, "保存 {Date} 调休临时课表失败，已撤销，稍后重试", key);
                continue;
            }
            state.Set(key, new MakeupRecord(overlayId, weekday));
            stateChanged = true;
            changed = true;
            logger.LogInformation("{Date} 调休上学，已建立临时课表（上 {Source} 的课）", key, Key(source));
        }
        return stateChanged;
    }

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
