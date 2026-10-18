namespace RemoteCI.Server.Data;

public sealed class WebPushJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public WebPushSubscription Subscription { get; set; } = null!;
    public string SourceId { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid? ClassId { get; set; }
    public int EventBit { get; set; }
    public Guid? NotificationId { get; set; }
    public long DueAt { get; set; }
    public long ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public bool Finished { get; set; }
}

public sealed class CalendarSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public Guid? ClassId { get; set; }
    public int? ReminderMinutes { get; set; } = 10;
    public int OffsetMinutes { get; set; } = 480;
    public bool Revoked { get; set; }
}

public sealed class CalendarEventVersion
{
    public Guid SubscriptionId { get; set; }
    public CalendarSubscription Subscription { get; set; } = null!;
    public string Uid { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
    public long UpdatedAt { get; set; }
    public long EndsAt { get; set; }
    public bool Cancelled { get; set; }
}
