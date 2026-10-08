namespace RemoteCI.Server.Data;

/// <summary>某一年 holiday-cn 数据最近一次拉取成功的原文；服务端离线重启后据此继续组装调休日历。</summary>
public sealed class HolidayYearSnapshot
{
    public int Year { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>管理员对某个调休上学日补哪天课的手动安排；FollowWeekday 为 null 表示不补课。</summary>
public sealed class HolidayMakeupOverride
{
    public DateOnly Date { get; set; }
    public int? FollowWeekday { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
