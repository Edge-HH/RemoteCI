namespace RemoteCI.Server.Services;

/// <summary>教室端本地日期：按该班最近状态快照上报的时区偏移计算；没有快照时用服务端本地时区。</summary>
public static class ClassClock
{
    public static DateOnly Today(IStateStore state, Guid classId, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var offset = state.GetLatestSnapshot(classId)?.TimeZoneOffsetMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : TimeZoneInfo.Local.GetUtcOffset(instant);
        return DateOnly.FromDateTime(instant.ToOffset(offset).DateTime);
    }
}
