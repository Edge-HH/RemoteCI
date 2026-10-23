namespace RemoteCI.Server.Data;

/// <summary>
/// 账号私有的自定义课程：每周 <see cref="WeekDay"/> 的固定时段，按学期教学周做单双周/多周轮换。
/// <see cref="Date"/> 有值时是只在那一天出现的单次课程（旧版数据或旧客户端按起止时间创建）。
/// </summary>
public sealed class PersonalCourse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Subject { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    /// <summary>0=周日 … 6=周六，与 ClassIsland TimeRule.WeekDay 相同。</summary>
    public int WeekDay { get; set; }
    /// <summary>当天开始时刻（距 0 点的分钟数，课程时区的本地时间）。</summary>
    public int StartMinutes { get; set; }
    /// <summary>当天结束时刻（分钟数），晚于开始时刻且在同一天内（不超过 1439）。</summary>
    public int EndMinutes { get; set; }
    public int WeekCountDiv { get; set; }
    public int WeekCountDivTotal { get; set; } = 2;
    public DateOnly? Date { get; set; }
    public int TimeZoneOffsetMinutes { get; set; }
    public int? ReminderMinutes { get; set; }
    public bool NotifyAtStart { get; set; }
    /// <summary>已发送课前提醒的那次课的开始时刻（Unix 毫秒）；同一次课不重复提醒。</summary>
    public long UpcomingSentFor { get; set; }
    /// <summary>已发送上课提醒的那次课的开始时刻（Unix 毫秒）。</summary>
    public long StartSentFor { get; set; }
}
