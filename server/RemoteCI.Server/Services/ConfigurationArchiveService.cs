using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class ConfigurationArchiveService(
    AppDbContext db,
    IStateStore state,
    IOptions<ServerOptions> options,
    IHostEnvironment environment)
{
    private static readonly byte[] Magic = "RCICFG01"u8.ToArray();
    private const int Iterations = 600_000;
    private const int MaxImportBytes = 64 * 1024 * 1024;
    private readonly string _backupDirectory = Path.Combine(
        Path.GetDirectoryName(Path.IsPathRooted(options.Value.DatabasePath)
            ? options.Value.DatabasePath
            : Path.Combine(environment.ContentRootPath, options.Value.DatabasePath))!, "backups");

    public async Task<ConfigurationSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var roles = await db.AccountRoles.AsNoTracking().Select(x => new RoleSnapshot(x.Id, x.Name, x.Kind, x.DefaultPermissions, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(x => new UserSnapshot(
            x.Id, x.UserName!, x.NormalizedUserName!, x.DisplayName, x.PasswordHash!, x.SecurityStamp!, x.ConcurrencyStamp!,
            x.Role, x.RoleDefinitionId, x.GrantedPermissions, x.Enabled, x.Version, x.UpdatedAt, x.IsSystemOwner)).ToListAsync(ct);
        var groups = await db.ClassGroups.AsNoTracking().Select(x => new GroupSnapshot(x.Id, x.Name, x.ParentId, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        var classrooms = await db.Classrooms.AsNoTracking().Select(x => new ClassroomSnapshot(x.Id, x.Name, x.VisitorAccessEnabled, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        var memberships = await db.ClassMemberships.AsNoTracking().Select(x => new MembershipSnapshot(x.UserId, x.ClassroomId, x.RoleDefinitionId)).ToListAsync(ct);
        var plugins = await db.PluginCredentials.AsNoTracking()
            .Select(x => new PluginSnapshot(
                x.Id, x.Name, x.TokenHash, x.Enabled, x.CreatedAt, x.LastSeenAt, x.ClassroomId,
                x.Assigned, x.ClassNameRemark))
            .ToListAsync(ct);
        var apiKeys = await db.UserApiKeys.AsNoTracking().Select(x => new ApiKeySnapshot(
            x.Id, x.UserId, x.Name, x.KeyHash, x.Prefix, x.CreatedAt, x.LastUsedAt, x.ExpiresAt, x.RevokedAt)).ToListAsync(ct);
        var extensionPolicies = await db.ExtensionPolicies.AsNoTracking()
            .Select(x => new ExtensionPolicySnapshot(x.ExtensionId, x.Enabled, x.AllowNonAdmin, x.UpdatedAt)).ToListAsync(ct);
        var extensionGroupPolicies = await db.ExtensionGroupPolicies.AsNoTracking()
            .Select(x => new ExtensionGroupPolicySnapshot(x.GroupId, x.AllowClassAdmin, x.UpdatedAt)).ToListAsync(ct);
        var extensionPreferences = await db.UserExtensionPreferences.AsNoTracking()
            .Select(x => new ExtensionPreferenceSnapshot(x.UserId, x.ExtensionId, x.ShowOnWatch, x.UpdatedAt)).ToListAsync(ct);
        var metadata = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var backup = await db.BackupConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var profiles = await db.StoredProfiles.AsNoTracking().Select(x => new StoredProfileSnapshot(
            x.Id, x.Name, x.ProfileJson, x.Revision, x.ClassId, x.SourceTemplateId, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        var holidayOverrides = await db.HolidayMakeupOverrides.AsNoTracking().OrderBy(x => x.Date)
            .Select(x => new HolidayOverrideSnapshot(x.Date, x.FollowWeekday)).ToListAsync(ct);
        return new ConfigurationSnapshot(5, DateTimeOffset.UtcNow, roles, users, plugins,
            new MetadataSnapshot(
                metadata.AccountVersion,
                metadata.ForceSenderInTitle,
                metadata.SchedulePullIntervalMinutes,
                AutoEnterVisitorPage: metadata.AutoEnterVisitorPage,
                LoginTheme: metadata.LoginTheme,
                LoginBackgroundOpacity: metadata.LoginBackgroundOpacity,
                LoginCardPosition: metadata.LoginCardPosition,
                MobileServerUrl: metadata.MobileServerUrl,
                ClassSelfService: new ClassSelfServicePolicy(
                    metadata.ClassAdminCanRename,
                    metadata.ClassAdminCanChangeAvatar,
                    metadata.ClassAdminCanPullSchedule)),
            new BackupSettingsSnapshot(backup.Enabled, backup.Cadence, backup.TimeOfDay, backup.DayOfWeek, backup.MaxBackups),
            // 课表按班级缓存在 ClassStateCaches，插件重连后自动同步，不再写入配置包。
            null, extensionPolicies, extensionPreferences,
            classrooms, memberships, groups, apiKeys, profiles,
            new HolidaySettingsSnapshot(metadata.HolidayCalendarEnabled, metadata.HolidaySourceUrlTemplate, holidayOverrides),
            extensionGroupPolicies);
    }

    public async Task<BackupFileInfo> CreateLocalBackupAsync(string source, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_backupDirectory);
        var snapshot = await CaptureAsync(ct);
        var payload = Compress(snapshot);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var safeSource = new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var name = $"remoteci-{stamp}-{safeSource}.rcibak";
        var finalPath = Path.Combine(_backupDirectory, name);
        var tempPath = finalPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, payload, ct);
        File.Move(tempPath, finalPath, true);
        await PruneAsync(ct);
        return ToInfo(new FileInfo(finalPath));
    }

    public async Task<byte[]> ExportEncryptedAsync(string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var compressed = Compress(await CaptureAsync(ct));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[compressed.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, compressed, cipher, tag, Magic);
        using var output = new MemoryStream();
        output.Write(Magic); output.Write(BitConverter.GetBytes(Iterations)); output.Write(salt); output.Write(nonce); output.Write(tag); output.Write(cipher);
        CryptographicOperations.ZeroMemory(key);
        return output.ToArray();
    }

    public ConfigurationSnapshot ReadEncrypted(ReadOnlySpan<byte> bytes, string password)
    {
        ValidatePassword(password);
        if (bytes.Length > MaxImportBytes || bytes.Length < 56 || !bytes[..Magic.Length].SequenceEqual(Magic)) throw new InvalidDataException("Invalid configuration package");
        var iterations = BitConverter.ToInt32(bytes.Slice(8, 4));
        if (iterations is < 100_000 or > 2_000_000) throw new InvalidDataException("Unsupported key derivation parameters");
        var salt = bytes.Slice(12, 16).ToArray(); var nonce = bytes.Slice(28, 12).ToArray(); var tag = bytes.Slice(40, 16).ToArray(); var cipher = bytes[56..].ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(nonce, cipher, tag, plain, Magic); }
        catch (CryptographicException) { throw new InvalidDataException("Wrong password or damaged configuration package"); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Decompress(plain);
    }

    public async Task ApplyAsync(ConfigurationSnapshot snapshot, CancellationToken ct = default)
    {
        Validate(snapshot);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // 显式删除自引用对象，恢复前不遗留模板或班级副本；旧配置包恢复为空档案库。
        await db.StoredProfiles.ExecuteDeleteAsync(ct);
        await db.DeviceSessions.ExecuteDeleteAsync(ct);
        await db.UserApiKeys.ExecuteDeleteAsync(ct);
        await db.PluginPairingCodes.ExecuteDeleteAsync(ct);
        await db.UserExtensionPreferences.ExecuteDeleteAsync(ct);
        await db.ExtensionPolicies.ExecuteDeleteAsync(ct);
        await db.ExtensionGroupPolicies.ExecuteDeleteAsync(ct);
        await db.Users.ExecuteDeleteAsync(ct);
        await db.AccountRoles.ExecuteDeleteAsync(ct);
        await db.PluginCredentials.ExecuteDeleteAsync(ct);
        await db.ClassMemberships.ExecuteDeleteAsync(ct);
        await db.Classrooms.ExecuteDeleteAsync(ct);
        await db.ClassGroups.ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();
        db.AccountRoles.AddRange(snapshot.Roles.Select(x => new AccountRole { Id=x.Id, Name=x.Name, NormalizedName=x.Name.Trim().ToUpperInvariant(), Kind=x.Kind, DefaultPermissions=UpgradeImportedPermissions(snapshot.Version, x.DefaultPermissions), CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt }));
        await db.SaveChangesAsync(ct);
        db.Users.AddRange(snapshot.Users.Select(x => new AppUser { Id=x.Id, UserName=x.Username, NormalizedUserName=x.NormalizedUsername, DisplayName=x.DisplayName, PasswordHash=x.PasswordHash, SecurityStamp=x.SecurityStamp, ConcurrencyStamp=x.ConcurrencyStamp, Role=x.Role, RoleDefinitionId=x.RoleId, GrantedPermissions=UpgradeImportedPermissions(snapshot.Version, x.GrantedPermissions), Enabled=x.Enabled, Version=x.Version, UpdatedAt=x.UpdatedAt, EmailConfirmed=false, PhoneNumberConfirmed=false, TwoFactorEnabled=false, LockoutEnabled=true, IsSystemOwner=x.IsSystemOwner == true }));
        db.UserApiKeys.AddRange((snapshot.ApiKeys ?? []).Select(x => new UserApiKey { Id=x.Id, UserId=x.UserId, Name=x.Name, KeyHash=x.KeyHash, Prefix=x.Prefix, CreatedAt=x.CreatedAt, LastUsedAt=x.LastUsedAt, ExpiresAt=x.ExpiresAt, RevokedAt=x.RevokedAt }));
        // v1/v2 旧包没有班级数据：建一个承载旧数据的班级（沿用旧版默认班级 Id），访客开关沿用包内全局设置。
        var legacyPackage = snapshot.Classrooms is null;
        var classrooms = (snapshot.Classrooms ?? []).ToList();
        if (legacyPackage)
        {
            classrooms.Add(new ClassroomSnapshot(
                Classroom.LegacyDefaultId, "默认班级",
                snapshot.Metadata.VisitorAccessEnabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        var groups = (snapshot.ClassGroups ?? []).ToList();
        // 分组支持层级：父分组必须先插入（按快照内推算的深度排序）。
        var byId = groups.ToDictionary(x => x.Id, x => x);
        int DepthOf(GroupSnapshot g)
        {
            var depth = 1;
            var parent = g.ParentId;
            while (parent is { } current && byId.TryGetValue(current, out var found))
            {
                depth++;
                parent = found.ParentId;
            }
            return depth;
        }
        db.ClassGroups.AddRange(groups.OrderBy(DepthOf).Select(x => new ClassGroup { Id=x.Id, Name=x.Name, ParentId=x.ParentId, CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt }));
        db.Classrooms.AddRange(classrooms.Select(x => new Classroom { Id=x.Id, Name=x.Name, VisitorAccessEnabled=x.VisitorAccessEnabled, CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt }));
        var memberships = (snapshot.Memberships ?? []).ToList();
        if (snapshot.Memberships is null)
        {
            // 旧包：为每个用户按其全局角色在默认班级补建成员关系，保持升级前权限不变。
            memberships = snapshot.Users.Select(x => new MembershipSnapshot(x.Id, Classroom.LegacyDefaultId, x.RoleId)).ToList();
        }
        db.ClassMemberships.AddRange(memberships.Select(x => new ClassMembership { UserId=x.UserId, ClassroomId=x.ClassroomId, RoleDefinitionId=x.RoleDefinitionId }));
        // 旧配置包没有 Assigned 字段：缺省视为已分配，避免导入后设备全部掉进“未分配”。
        db.PluginCredentials.AddRange(snapshot.Plugins.Select(x => new PluginCredential
        {
            Id=x.Id, Name=x.Name, TokenHash=x.TokenHash, Enabled=x.Enabled,
            // 旧包没有班级字段：设备归到承载旧数据的班级；新包中未分配设备的班级为 null。
            ClassroomId=legacyPackage ? x.ClassroomId ?? Classroom.LegacyDefaultId : (x.Assigned ?? true) ? x.ClassroomId : null,
            CreatedAt=x.CreatedAt, LastSeenAt=x.LastSeenAt,
            Assigned=(x.Assigned ?? true) && (legacyPackage || x.ClassroomId is not null), ClassNameRemark=x.ClassNameRemark,
        }));
        db.ExtensionPolicies.AddRange((snapshot.ExtensionPolicies ?? []).Select(x => new ExtensionPolicy { ExtensionId=x.ExtensionId, Enabled=x.Enabled, AllowNonAdmin=x.AllowNonAdmin, UpdatedAt=x.UpdatedAt }));
        // 旧配置包没有逐插件的班级自治开关，恢复后全部插件回到默认的“仅系统管理员管理”。
        db.ExtensionGroupPolicies.AddRange((snapshot.ExtensionGroupPolicies ?? [])
            .Where(x => ExtensionGroupPolicyService.IsValidGroupId(x.GroupId))
            .DistinctBy(x => x.GroupId, StringComparer.Ordinal)
            .Select(x => new ExtensionGroupPolicy { GroupId = x.GroupId, AllowClassAdmin = x.AllowClassAdmin, UpdatedAt = x.UpdatedAt }));
        db.UserExtensionPreferences.AddRange((snapshot.ExtensionPreferences ?? []).Select(x => new UserExtensionPreference { UserId=x.UserId, ExtensionId=x.ExtensionId, ShowOnWatch=x.ShowOnWatch, UpdatedAt=x.UpdatedAt }));
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.AccountVersion = snapshot.Metadata.AccountVersion + 1;
        metadata.ForceSenderInTitle = snapshot.Metadata.ForceSenderInTitle;
        metadata.SchedulePullIntervalMinutes = snapshot.Metadata.SchedulePullIntervalMinutes;
        // autoEnter 只在存在开放访客的班级时有效；v1/v2 旧包的全局访客开关已映射到默认班级。
        metadata.AutoEnterVisitorPage =
            (classrooms.Any(x => x.VisitorAccessEnabled) || snapshot.Metadata.VisitorAccessEnabled) &&
            snapshot.Metadata.AutoEnterVisitorPage;
        metadata.LoginTheme = snapshot.Metadata.LoginTheme;
        metadata.LoginBackgroundOpacity = Math.Clamp(snapshot.Metadata.LoginBackgroundOpacity, 0, 100);
        metadata.LoginCardPosition = Enum.IsDefined(snapshot.Metadata.LoginCardPosition) ? snapshot.Metadata.LoginCardPosition : LoginCardPosition.Center;
        try { metadata.MobileServerUrl = MobileLoginSettings.Normalize(snapshot.Metadata.MobileServerUrl); }
        catch (ArgumentException) { metadata.MobileServerUrl = null; }
        // 旧配置包没有班级自治策略，按升级前行为恢复。
        var selfService = snapshot.Metadata.ClassSelfService ?? ClassSelfServicePolicy.Default;
        metadata.ClassAdminCanRename = selfService.CanRename;
        metadata.ClassAdminCanChangeAvatar = selfService.CanChangeAvatar;
        metadata.ClassAdminCanPullSchedule = selfService.CanPullSchedule;
        // 旧配置包没有调休字段时保留当前调休设置，节假日数据快照不随配置包迁移，恢复后会重新拉取。
        if (snapshot.Holidays is { } holidays)
        {
            metadata.HolidayCalendarEnabled = holidays.Enabled;
            metadata.HolidaySourceUrlTemplate = HolidaySettingsStore.NormalizeTemplate(holidays.SourceUrlTemplate);
            await db.HolidayMakeupOverrides.ExecuteDeleteAsync(ct);
            db.HolidayMakeupOverrides.AddRange(holidays.Overrides.Select(x => new HolidayMakeupOverride
            {
                Date = x.Date, FollowWeekday = x.FollowWeekday, UpdatedAt = DateTimeOffset.UtcNow,
            }));
        }
        var backup = await db.BackupConfigurations.SingleAsync(x => x.Id == 1, ct);
        backup.Enabled=snapshot.Backup.Enabled; backup.Cadence=snapshot.Backup.Cadence; backup.TimeOfDay=snapshot.Backup.TimeOfDay; backup.DayOfWeek=snapshot.Backup.DayOfWeek; backup.MaxBackups=Math.Clamp(snapshot.Backup.MaxBackups,1,100); backup.LastScheduledAt=null; backup.LastSucceededAt=null; backup.LastError=null;
        await db.SaveChangesAsync(ct);
        var profiles = snapshot.Profiles ?? [];
        // 来源模板须先落库，否则班级副本的自引用外键无效。
        db.StoredProfiles.AddRange(profiles.Where(x => x.ClassId is null).Select(RestoreProfile));
        await db.SaveChangesAsync(ct);
        db.StoredProfiles.AddRange(profiles.Where(x => x.ClassId is not null).Select(RestoreProfile));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (snapshot.Schedule is not null && snapshot.Classrooms is null) state.SaveSchedule(Classroom.LegacyDefaultId, snapshot.Schedule);
    }

    public IReadOnlyList<BackupFileInfo> ListBackups() { Directory.CreateDirectory(_backupDirectory); return Directory.EnumerateFiles(_backupDirectory,"*.rcibak").Select(x=>ToInfo(new FileInfo(x))).OrderByDescending(x=>x.CreatedAt).ToList(); }
    public byte[] ReadBackup(string name) => File.ReadAllBytes(ResolveBackup(name));
    public ConfigurationSnapshot ParseLocalBackup(byte[] bytes) => Decompress(bytes);
    public void DeleteBackup(string name) => File.Delete(ResolveBackup(name));

    private string ResolveBackup(string name) { var safe=Path.GetFileName(name); var path=Path.GetFullPath(Path.Combine(_backupDirectory,safe)); if (!path.StartsWith(Path.GetFullPath(_backupDirectory),StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new FileNotFoundException(); return path; }
    private async Task PruneAsync(CancellationToken ct) { var max=(await db.BackupConfigurations.AsNoTracking().SingleAsync(x=>x.Id==1,ct)).MaxBackups; foreach(var file in Directory.EnumerateFiles(_backupDirectory,"*.rcibak").Select(x=>new FileInfo(x)).OrderByDescending(x=>x.CreationTimeUtc).Skip(Math.Clamp(max,1,100))) file.Delete(); }
    private static byte[] Compress(ConfigurationSnapshot snapshot) { var json=JsonSerializer.SerializeToUtf8Bytes(snapshot,JsonDefaults.Options); using var output=new MemoryStream(); using(var gzip=new GZipStream(output,CompressionLevel.SmallestSize,true)) gzip.Write(json); return output.ToArray(); }
    private static ConfigurationSnapshot Decompress(byte[] bytes) { using var input=new MemoryStream(bytes); using var gzip=new GZipStream(input,CompressionMode.Decompress); return JsonSerializer.Deserialize<ConfigurationSnapshot>(gzip,JsonDefaults.Options) ?? throw new InvalidDataException("Invalid backup"); }
    private static BackupFileInfo ToInfo(FileInfo file) => new(file.Name,file.CreationTimeUtc,file.Length,file.Name.Contains("preimport",StringComparison.OrdinalIgnoreCase)?"Import":"Backup");
    private static void ValidatePassword(string value) { if (value.Length < 8) throw new InvalidDataException("Export password must contain at least 8 characters"); }
    private static UserPermissions UpgradeImportedPermissions(int snapshotVersion, UserPermissions permissions) =>
        snapshotVersion == 1 && permissions.HasFlag(UserPermissions.PowerControl)
            ? permissions | UserPermissions.MainMenuControl
            : permissions;
    private static void Validate(ConfigurationSnapshot value)
    {
        if(value.Version is not (1 or 2 or 3 or 4 or 5) || value.Roles.Count==0 || value.Users.Count==0) throw new InvalidDataException("Invalid backup schema");
        var roleIds=value.Roles.Select(x=>x.Id).ToHashSet();
        if(value.Users.Any(x=>!roleIds.Contains(x.RoleId))) throw new InvalidDataException("Unknown role reference");
        if(!value.Users.Any(x=>x.Enabled && x.Role==UserRole.Admin)) throw new InvalidDataException("At least one enabled administrator is required");
        if(value.Users.Select(x=>x.Username.ToUpperInvariant()).Distinct().Count()!=value.Users.Count) throw new InvalidDataException("Duplicate account ID");
        var userIds=value.Users.Select(x=>x.Id).ToHashSet();
        if((value.ExtensionPreferences??[]).Any(x=>!userIds.Contains(x.UserId))) throw new InvalidDataException("Unknown extension preference user");
        if((value.Memberships??[]).Any(x=>!userIds.Contains(x.UserId))) throw new InvalidDataException("Unknown membership user");
        if((value.ApiKeys??[]).Any(x=>!userIds.Contains(x.UserId))) throw new InvalidDataException("Unknown API key user");
        var classroomIds=(value.Classrooms??[]).Select(x=>x.Id).ToHashSet();
        if((value.Memberships??[]).Any(x=>!classroomIds.Contains(x.ClassroomId))) throw new InvalidDataException("Unknown membership classroom");
        var profiles=value.Profiles??[];
        if(profiles.Select(x=>x.Id).Distinct().Count()!=profiles.Count || profiles.Where(x=>x.ClassId is not null).GroupBy(x=>x.ClassId).Any(x=>x.Count()>1))
            throw new InvalidDataException("Duplicate profile or classroom profile");
        var templates=profiles.Where(x=>x.ClassId is null).Select(x=>x.Id).ToHashSet();
        foreach(var profile in profiles)
        {
            if(profile.Revision<1 || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length>100 ||
                profile.ClassId is { } id && !classroomIds.Contains(id) ||
                profile.SourceTemplateId is { } templateId && (profile.ClassId is null || !templates.Contains(templateId)) ||
                ProfileDocument.Validate(profile.ProfileJson).Count>0)
                throw new InvalidDataException("Invalid stored profile");
        }
        if(value.Holidays is { } holidays)
        {
            try { HolidaySettingsStore.NormalizeTemplate(holidays.SourceUrlTemplate); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid holiday source"); }
            if(holidays.Overrides is null || holidays.Overrides.Any(x=>x.FollowWeekday is < 1 or > 5) ||
                holidays.Overrides.Select(x=>x.Date).Distinct().Count()!=holidays.Overrides.Count)
                throw new InvalidDataException("Invalid holiday overrides");
        }
    }
    private static StoredProfile RestoreProfile(StoredProfileSnapshot x) => new()
    {
        Id=x.Id, Name=x.Name, ProfileJson=x.ProfileJson, Revision=x.Revision, ClassId=x.ClassId,
        SourceTemplateId=x.SourceTemplateId, CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt,
    };
}

public sealed record BackupFileInfo(string Name, DateTimeOffset CreatedAt, long Size, string Source);
public sealed record ConfigurationSnapshot(int Version, DateTimeOffset CreatedAt, List<RoleSnapshot> Roles, List<UserSnapshot> Users, List<PluginSnapshot> Plugins, MetadataSnapshot Metadata, BackupSettingsSnapshot Backup, ScheduleBundle? Schedule, List<ExtensionPolicySnapshot>? ExtensionPolicies = null, List<ExtensionPreferenceSnapshot>? ExtensionPreferences = null, List<ClassroomSnapshot>? Classrooms = null, List<MembershipSnapshot>? Memberships = null, List<GroupSnapshot>? ClassGroups = null, List<ApiKeySnapshot>? ApiKeys = null, List<StoredProfileSnapshot>? Profiles = null, HolidaySettingsSnapshot? Holidays = null, List<ExtensionGroupPolicySnapshot>? ExtensionGroupPolicies = null);
public sealed record HolidaySettingsSnapshot(bool Enabled, string? SourceUrlTemplate, List<HolidayOverrideSnapshot> Overrides);
public sealed record HolidayOverrideSnapshot(DateOnly Date, int? FollowWeekday);
public sealed record StoredProfileSnapshot(Guid Id, string Name, string ProfileJson, long Revision, Guid? ClassId, Guid? SourceTemplateId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record RoleSnapshot(Guid Id,string Name,AccountRoleKind Kind,UserPermissions DefaultPermissions,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
public sealed record UserSnapshot(Guid Id,string Username,string NormalizedUsername,string DisplayName,string PasswordHash,string SecurityStamp,string ConcurrencyStamp,UserRole Role,Guid RoleId,UserPermissions GrantedPermissions,bool Enabled,long Version,DateTimeOffset UpdatedAt,bool? IsSystemOwner = null);
public sealed record ClassroomSnapshot(Guid Id,string Name,bool VisitorAccessEnabled,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
public sealed record GroupSnapshot(Guid Id,string Name,Guid? ParentId,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
public sealed record MembershipSnapshot(Guid UserId,Guid ClassroomId,Guid RoleDefinitionId);
public sealed record PluginSnapshot(
    Guid Id, string Name, string TokenHash, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt,
    Guid? ClassroomId = null, bool? Assigned = null, string? ClassNameRemark = null);
public sealed record ApiKeySnapshot(Guid Id,Guid UserId,string Name,string KeyHash,string Prefix,DateTimeOffset CreatedAt,DateTimeOffset? LastUsedAt,DateTimeOffset? ExpiresAt,DateTimeOffset? RevokedAt);
public sealed record ExtensionPolicySnapshot(string ExtensionId,bool Enabled,bool AllowNonAdmin,DateTimeOffset UpdatedAt);
public sealed record ExtensionGroupPolicySnapshot(string GroupId, bool AllowClassAdmin, DateTimeOffset UpdatedAt);
public sealed record ExtensionPreferenceSnapshot(Guid UserId,string ExtensionId,bool ShowOnWatch,DateTimeOffset UpdatedAt);
public sealed record MetadataSnapshot(
    long AccountVersion,
    bool ForceSenderInTitle,
    int SchedulePullIntervalMinutes,
    // v1/v2 旧包字段：VisitorAccessEnabled 是升级前的全局开关，v3 起按班级存储，导入时映射到默认班级。
    bool VisitorAccessEnabled = false,
    bool AutoEnterVisitorPage = false,
    // 登录页外观标量设置；背景图片体积较大，与班级头像一样不进入配置包。
    LoginTheme LoginTheme = LoginTheme.Follow,
    int LoginBackgroundOpacity = 100,
    LoginCardPosition LoginCardPosition = LoginCardPosition.Center,
    // 手机扫码登录二维码中的服务器地址；null 表示使用访问地址。
    string? MobileServerUrl = null,
    // 班级自治策略；旧包缺失时按 ClassSelfServicePolicy.Default 恢复。
    ClassSelfServicePolicy? ClassSelfService = null);
public sealed record BackupSettingsSnapshot(bool Enabled,BackupCadence Cadence,TimeSpan TimeOfDay,DayOfWeek DayOfWeek,int MaxBackups);
