using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<WebLoginTicket> WebLoginTickets => Set<WebLoginTicket>();
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<PluginCredential> PluginCredentials => Set<PluginCredential>();
    public DbSet<PluginPairingCode> PluginPairingCodes => Set<PluginPairingCode>();
    public DbSet<SystemMetadata> SystemMetadata => Set<SystemMetadata>();
    public DbSet<AccountRole> AccountRoles => Set<AccountRole>();
    public DbSet<BackupConfiguration> BackupConfigurations => Set<BackupConfiguration>();
    public DbSet<ExtensionPolicy> ExtensionPolicies => Set<ExtensionPolicy>();
    public DbSet<ExtensionGroupPolicy> ExtensionGroupPolicies => Set<ExtensionGroupPolicy>();
    public DbSet<UserExtensionPreference> UserExtensionPreferences => Set<UserExtensionPreference>();
    public DbSet<PendingExtensionSetting> PendingExtensionSettings => Set<PendingExtensionSetting>();
    public DbSet<ClassStateCache> ClassStateCaches => Set<ClassStateCache>();
    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<ClassMembership> ClassMemberships => Set<ClassMembership>();
    public DbSet<ClassGroup> ClassGroups => Set<ClassGroup>();
    public DbSet<ClassGroupAssignment> ClassGroupAssignments => Set<ClassGroupAssignment>();
    public DbSet<ScheduleSwapRequest> ScheduleSwapRequests => Set<ScheduleSwapRequest>();
    public DbSet<LessonTeacherOverride> LessonTeacherOverrides => Set<LessonTeacherOverride>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<WebPushSubscription> WebPushSubscriptions => Set<WebPushSubscription>();
    public DbSet<StoredProfile> StoredProfiles => Set<StoredProfile>();
    public DbSet<HolidayYearSnapshot> HolidayYearSnapshots => Set<HolidayYearSnapshot>();
    public DbSet<HolidayMakeupOverride> HolidayMakeupOverrides => Set<HolidayMakeupOverride>();
    public DbSet<PersonalCourse> PersonalCourses => Set<PersonalCourse>();
    public DbSet<WebPushJob> WebPushJobs => Set<WebPushJob>();
    public DbSet<CalendarSubscription> CalendarSubscriptions => Set<CalendarSubscription>();
    public DbSet<CalendarEventVersion> CalendarEventVersions => Set<CalendarEventVersion>();
    public DbSet<CommandAuditEntry> CommandAuditEntries => Set<CommandAuditEntry>();
    public DbSet<DevicePresenceEvent> DevicePresenceEvents => Set<DevicePresenceEvent>();
    public DbSet<DeviceAlertState> DeviceAlertStates => Set<DeviceAlertState>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();
    public DbSet<StoredProfileVersion> StoredProfileVersions => Set<StoredProfileVersion>();
    public DbSet<ProfileDriftReport> ProfileDriftReports => Set<ProfileDriftReport>();
    public DbSet<LessonLedgerEntry> LessonLedgerEntries => Set<LessonLedgerEntry>();
    public DbSet<ActionLinkToken> ActionLinkTokens => Set<ActionLinkToken>();
    public DbSet<ClassBoard> ClassBoards => Set<ClassBoard>();
    public DbSet<AppGuardOverride> AppGuardOverrides => Set<AppGuardOverride>();
    public DbSet<AppGuardEventRecord> AppGuardEvents => Set<AppGuardEventRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<DevicePresenceEvent>(entity =>
        {
            entity.HasIndex(x => new { x.CredentialId, x.AtUnixMs });
            entity.HasIndex(x => x.AtUnixMs);
        });
        builder.Entity<StoredProfileVersion>(entity =>
        {
            entity.HasIndex(x => new { x.ProfileId, x.Revision });
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.SavedByName).HasMaxLength(40);
            entity.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ClassBoard>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.ClassId);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ActionLinkToken>(entity =>
        {
            entity.HasKey(x => x.TokenHash);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.Property(x => x.Action).HasMaxLength(40);
            entity.HasIndex(x => x.ExpiresAtUnixMs);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LessonLedgerEntry>(entity =>
        {
            entity.HasIndex(x => new { x.ClassId, x.Date, x.Index }).IsUnique();
            entity.HasIndex(x => x.Date);
            entity.Property(x => x.ClassName).HasMaxLength(40);
            entity.Property(x => x.Date).HasMaxLength(10);
            entity.Property(x => x.Label).HasMaxLength(40);
            entity.Property(x => x.Subject).HasMaxLength(100);
            entity.Property(x => x.StartTime).HasMaxLength(8);
            entity.Property(x => x.EndTime).HasMaxLength(8);
            entity.Property(x => x.TeacherName).HasMaxLength(100);
            entity.Property(x => x.DefaultTeacherName).HasMaxLength(100);
        });
        builder.Entity<ProfileDriftReport>(entity =>
        {
            entity.HasKey(x => x.ClassId);
            entity.Property(x => x.ClassId).ValueGeneratedNever();
        });
        builder.Entity<SystemMetadata>(entity => entity.Property(x => x.ProfileDriftCheckTime).HasMaxLength(5));
        builder.Entity<AutomationRule>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(60);
            entity.Property(x => x.CreatedByName).HasMaxLength(40);
            entity.Property(x => x.ValidFrom).HasMaxLength(10);
            entity.Property(x => x.ValidTo).HasMaxLength(10);
            entity.Property(x => x.LastResult).HasMaxLength(200);
        });
        builder.Entity<AutomationRun>(entity =>
        {
            entity.HasIndex(x => x.DedupeKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.DueAtUnixMs });
            entity.HasIndex(x => new { x.RuleId, x.DueAtUnixMs });
            entity.Property(x => x.DedupeKey).HasMaxLength(160);
            entity.Property(x => x.RuleName).HasMaxLength(60);
            entity.Property(x => x.ClassName).HasMaxLength(40);
            entity.Property(x => x.TriggerText).HasMaxLength(100);
            entity.Property(x => x.Message).HasMaxLength(1000);
            entity.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AppGuardOverride>(entity =>
        {
            entity.HasKey(x => x.CredentialId);
            entity.Property(x => x.CredentialId).ValueGeneratedNever();
            entity.HasOne(x => x.Credential).WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AppGuardEventRecord>(entity =>
        {
            entity.HasIndex(x => x.AtUnixMs);
            entity.HasIndex(x => new { x.CredentialId, x.AtUnixMs });
            entity.Property(x => x.DeviceName).HasMaxLength(200);
            entity.Property(x => x.Kind).HasMaxLength(40);
            entity.Property(x => x.Detail).HasMaxLength(1000);
        });
        builder.Entity<DeviceAlertState>(entity =>
        {
            entity.HasKey(x => x.CredentialId);
            entity.Property(x => x.CredentialId).ValueGeneratedNever();
        });
        builder.Entity<CommandAuditEntry>(entity =>
        {
            entity.HasIndex(x => x.CreatedAtUnixMs);
            entity.HasIndex(x => new { x.ClassId, x.CreatedAtUnixMs });
            entity.HasIndex(x => new { x.ActorUserId, x.CreatedAtUnixMs });
            entity.Property(x => x.MessageId).HasMaxLength(64);
            entity.Property(x => x.ActorName).HasMaxLength(100);
            entity.Property(x => x.SourceDetail).HasMaxLength(100);
            entity.Property(x => x.ClassName).HasMaxLength(40);
            entity.Property(x => x.DeviceName).HasMaxLength(80);
            entity.Property(x => x.Summary).HasMaxLength(CommandAuditLimits.TextLength);
            entity.Property(x => x.ResultCode).HasMaxLength(40);
            entity.Property(x => x.ResultMessage).HasMaxLength(CommandAuditLimits.TextLength);
        });
        builder.Entity<WebPushSubscription>().Property(x => x.BrowserId).HasMaxLength(64);
        builder.Entity<WebPushJob>(entity =>
        {
            entity.HasIndex(x => new { x.SubscriptionId, x.SourceId }).IsUnique();
            entity.HasIndex(x => new { x.Finished, x.DueAt });
            entity.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.SourceId).HasMaxLength(200);
        });
        builder.Entity<CalendarSubscription>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CalendarEventVersion>(entity =>
        {
            entity.HasKey(x => new { x.SubscriptionId, x.Uid });
            entity.Property(x => x.Uid).HasMaxLength(200);
            entity.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PersonalCourse>(entity =>
        {
            entity.Property(x => x.Subject).HasMaxLength(100);
            entity.Property(x => x.Location).HasMaxLength(200);
            entity.HasIndex(x => new { x.UserId, x.StartsAtUnixMs });
            entity.HasIndex(x => x.EndsAtUnixMs);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StoredProfile>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            // SQLite 的唯一索引允许多个 NULL：模板不限数量，班级只有一个工作副本。
            entity.HasIndex(x => x.ClassId).IsUnique();
            entity.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.SourceTemplate).WithMany().HasForeignKey(x => x.SourceTemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AppUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(40);
            entity.Property(x => x.SetupTokenHash).HasMaxLength(64);
            entity.HasIndex(x => x.Version);
            entity.HasOne(x => x.RoleDefinition).WithMany(x => x.Users).HasForeignKey(x => x.RoleDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<HolidayYearSnapshot>(entity =>
        {
            entity.HasKey(x => x.Year);
            // 年份是业务主键，不能让 SQLite 自增。
            entity.Property(x => x.Year).ValueGeneratedNever();
            entity.Property(x => x.SourceUrl).HasMaxLength(600);
        });
        builder.Entity<HolidayMakeupOverride>(entity => entity.HasKey(x => x.Date));
        builder.Entity<SystemMetadata>(entity =>
        {
            entity.Property(x => x.LoginBackgroundContentType).HasMaxLength(64);
            entity.Property(x => x.MobileServerUrl).HasMaxLength(512);
            entity.Property(x => x.HolidaySourceUrlTemplate).HasMaxLength(512);
        });
        builder.Entity<AccountRole>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.Property(x => x.NormalizedName).HasMaxLength(40);
            entity.HasIndex(x => x.NormalizedName).IsUnique();
        });
        builder.Entity<BackupConfiguration>(entity =>
        {
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        builder.Entity<ExtensionPolicy>(entity =>
        {
            entity.HasKey(x => x.ExtensionId);
            entity.Property(x => x.ExtensionId).HasMaxLength(ExtensionId.MaxLength);
        });
        builder.Entity<ExtensionGroupPolicy>(entity =>
        {
            entity.HasKey(x => x.GroupId);
            entity.Property(x => x.GroupId).HasMaxLength(ExtensionId.MaxLength);
        });
        builder.Entity<PendingExtensionSetting>(entity =>
        {
            entity.HasKey(x => new { x.ClassroomId, x.GroupId });
            entity.Property(x => x.GroupId).HasMaxLength(ExtensionId.MaxLength);
            entity.Property(x => x.ValuesJson).HasMaxLength(65536);
            entity.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ClassStateCache>(entity =>
        {
            entity.HasKey(x => x.ClassroomId);
            entity.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UserExtensionPreference>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.ExtensionId });
            entity.Property(x => x.ExtensionId).HasMaxLength(ExtensionId.MaxLength);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DeviceSession>(entity =>
        {
            entity.HasIndex(x => x.AccessTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt });
            entity.Property(x => x.DeviceName).HasMaxLength(80);
            entity.Property(x => x.VerifierHash).HasMaxLength(64);
            entity.Property(x => x.AccessTokenHash).HasMaxLength(64);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WebLoginTicket>(entity =>
        {
            entity.HasKey(x => x.TokenHash);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.ExpiresAtUnixMs);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UserApiKey>(entity =>
        {
            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt });
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.Property(x => x.KeyHash).HasMaxLength(64);
            entity.Property(x => x.Prefix).HasMaxLength(16);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PluginCredential>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.ClassNameRemark).HasMaxLength(40);
            entity.Property(x => x.SoftwareInventoryJson).HasMaxLength(65536);
            entity.HasOne(x => x.Classroom).WithMany().HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PluginPairingCode>(entity =>
        {
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.Property(x => x.CodeHash).HasMaxLength(64);
        });
        builder.Entity<Classroom>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.AvatarContentType).HasMaxLength(64);
        });
        builder.Entity<ClassGroup>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(40);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<ClassGroupAssignment>(entity =>
        {
            entity.HasKey(x => new { x.GroupId, x.ClassroomId });
            entity.HasIndex(x => x.ClassroomId);
            entity.HasOne(x => x.Group).WithMany(x => x.Assignments).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Classroom).WithMany(x => x.GroupAssignments).HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ClassMembership>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.ClassroomId });
            entity.HasIndex(x => x.ClassroomId);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Classroom).WithMany(x => x.Memberships).HasForeignKey(x => x.ClassroomId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.RoleDefinition).WithMany().HasForeignKey(x => x.RoleDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ScheduleSwapRequest>(entity =>
        {
            entity.HasIndex(x => new { x.Status, x.TargetDate });
            entity.HasIndex(x => x.RequesterUserId);
            entity.Property(x => x.Reason).HasMaxLength(200);
            entity.Property(x => x.DecisionNote).HasMaxLength(200);
            entity.Property(x => x.SourceDate).HasMaxLength(10);
            entity.Property(x => x.TargetDate).HasMaxLength(10);
            entity.Property(x => x.CounterpartDate).HasMaxLength(10);
            entity.Property(x => x.SourceSubject).HasMaxLength(100);
            entity.Property(x => x.TargetSubject).HasMaxLength(100);
            entity.Property(x => x.SourceTeacher).HasMaxLength(100);
            entity.Property(x => x.TargetTeacher).HasMaxLength(100);
            entity.Property(x => x.ReplacementSubjectName).HasMaxLength(100);
            entity.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LessonTeacherOverride>(entity =>
        {
            entity.HasIndex(x => new { x.ClassId, x.Date, x.Index }).IsUnique();
            entity.Property(x => x.Date).HasMaxLength(10);
            entity.Property(x => x.TeacherName).HasMaxLength(100);
            entity.Property(x => x.ExpectedSubject).HasMaxLength(100);
        });
        builder.Entity<UserNotification>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.Property(x => x.Kind).HasMaxLength(40);
            entity.Property(x => x.Title).HasMaxLength(120);
            entity.Property(x => x.Body).HasMaxLength(1000);
            entity.Property(x => x.Link).HasMaxLength(200);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WebPushSubscription>(entity =>
        {
            entity.HasIndex(x => x.Endpoint).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.Property(x => x.Endpoint).HasMaxLength(1024);
            entity.Property(x => x.P256dh).HasMaxLength(200);
            entity.Property(x => x.Auth).HasMaxLength(100);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
