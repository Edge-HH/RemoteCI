namespace RemoteCI.Server.Data;

public sealed class PersonalCourse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Subject { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public long StartsAtUnixMs { get; set; }
    public long EndsAtUnixMs { get; set; }
    public int TimeZoneOffsetMinutes { get; set; }
    public int? ReminderMinutes { get; set; }
    public bool NotifyAtStart { get; set; }
    public bool UpcomingSent { get; set; }
    public bool StartSent { get; set; }
}
