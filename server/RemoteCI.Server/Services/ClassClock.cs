namespace RemoteCI.Server.Services;

/// <summary>
/// 教室端本地时钟。课表钟点是教室电脑的本地时间，时区偏移依次取：本班最近状态快照上报的偏移 →
/// 任一班级最近上报的偏移（同一部署的教室电脑通常在同一时区；按档案推算课表的班级从未连接插件，没有自己的快照）→
/// 调用方提供的回退偏移（例如手机上报的本机时区）→ 服务端本地时区（Docker 容器默认是 UTC，因此放在最后）。
/// </summary>
public static class ClassClock
{
    /// <summary>DateTimeOffset 允许的最大时区偏移（±14 小时）。</summary>
    public const int MaxOffsetMinutes = 14 * 60;

    public static TimeSpan Offset(IStateStore state, Guid classId, DateTimeOffset instant, TimeSpan? fallback = null)
    {
        if (Valid(state.GetLatestSnapshot(classId)?.TimeZoneOffsetMinutes) is { } own) return own;
        if (Valid(state.GetLatestTimeZoneOffsetMinutes()) is { } shared) return shared;
        return fallback ?? TimeZoneInfo.Local.GetUtcOffset(instant);
    }

    public static DateOnly Today(IStateStore state, Guid classId, DateTimeOffset? now = null, TimeSpan? fallback = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        return DateOnly.FromDateTime(instant.ToOffset(Offset(state, classId, instant, fallback)).DateTime);
    }

    /// <summary>把上报的分钟数转换为可用于 DateTimeOffset 的偏移；超出 ±14 小时视为无效。</summary>
    public static TimeSpan? Valid(int? minutes) =>
        minutes is { } value && Math.Abs(value) <= MaxOffsetMinutes ? TimeSpan.FromMinutes(value) : null;
}
