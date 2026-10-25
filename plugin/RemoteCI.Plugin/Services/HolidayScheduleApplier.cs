using System.Globalization;
using System.Text.Json;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>放假日所需的宿主能力（ClassIsland 服务含 internal 成员，外部程序集无法实现假对象，故经此防腐层）。</summary>
public interface IHolidayHostOperations
{
    /// <summary>宿主 LessonsService 的“启用课表”运行时开关；宿主不支持时为 null。</summary>
    bool? IsClassPlanEnabled { get; set; }
}

/// <summary>
/// 按调休日历调整 ClassIsland 的放假日：关闭课表，放假结束后只恢复自己关掉的。
/// 调休上学日的临时层由 <see cref="ScheduleOverlayService"/> 与调课一起生成。必须在 UI 线程调用。
/// </summary>
public sealed class HolidayScheduleApplier(IHolidayHostOperations host, ILogger<HolidayScheduleApplier> logger)
{
    private bool _disabledByUs;
    private DateTime? _offDayHandled;
    private bool _warnedUnsupported;

    /// <summary>切换了“启用课表”开关时返回 true，调用方据此重新上报课表。</summary>
    public bool Apply(HolidayCalendar calendar, DateTime now)
    {
        var days = calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First())
            : new Dictionary<string, HolidayCalendarDay>();
        return ApplyOffDay(days, now.Date);
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

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
