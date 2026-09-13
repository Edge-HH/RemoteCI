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
            x.Role, x.RoleDefinitionId, x.GrantedPermissions, x.Enabled, x.Version, x.UpdatedAt)).ToListAsync(ct);
        var plugins = await db.PluginCredentials.AsNoTracking().Select(x => new PluginSnapshot(x.Id, x.Name, x.TokenHash, x.Enabled, x.CreatedAt, x.LastSeenAt)).ToListAsync(ct);
        var extensionPolicies = await db.ExtensionPolicies.AsNoTracking()
            .Select(x => new ExtensionPolicySnapshot(x.ExtensionId, x.Enabled, x.AllowNonAdmin, x.UpdatedAt)).ToListAsync(ct);
        var extensionPreferences = await db.UserExtensionPreferences.AsNoTracking()
            .Select(x => new ExtensionPreferenceSnapshot(x.UserId, x.ExtensionId, x.ShowOnWatch, x.UpdatedAt)).ToListAsync(ct);
        var metadata = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var backup = await db.BackupConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return new ConfigurationSnapshot(2, DateTimeOffset.UtcNow, roles, users, plugins,
            new MetadataSnapshot(
                metadata.AccountVersion,
                metadata.ForceSenderInTitle,
                metadata.SchedulePullIntervalMinutes,
                metadata.VisitorAccessEnabled,
                metadata.AutoEnterVisitorPage),
            new BackupSettingsSnapshot(backup.Enabled, backup.Cadence, backup.TimeOfDay, backup.DayOfWeek, backup.MaxBackups),
            state.GetLatestSchedule(), extensionPolicies, extensionPreferences);
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
        await db.DeviceSessions.ExecuteDeleteAsync(ct);
        await db.PluginPairingCodes.ExecuteDeleteAsync(ct);
        await db.UserExtensionPreferences.ExecuteDeleteAsync(ct);
        await db.ExtensionPolicies.ExecuteDeleteAsync(ct);
        await db.Users.ExecuteDeleteAsync(ct);
        await db.AccountRoles.ExecuteDeleteAsync(ct);
        await db.PluginCredentials.ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();
        db.AccountRoles.AddRange(snapshot.Roles.Select(x => new AccountRole { Id=x.Id, Name=x.Name, NormalizedName=x.Name.Trim().ToUpperInvariant(), Kind=x.Kind, DefaultPermissions=UpgradeImportedPermissions(snapshot.Version, x.DefaultPermissions), CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt }));
        await db.SaveChangesAsync(ct);
        db.Users.AddRange(snapshot.Users.Select(x => new AppUser { Id=x.Id, UserName=x.Username, NormalizedUserName=x.NormalizedUsername, DisplayName=x.DisplayName, PasswordHash=x.PasswordHash, SecurityStamp=x.SecurityStamp, ConcurrencyStamp=x.ConcurrencyStamp, Role=x.Role, RoleDefinitionId=x.RoleId, GrantedPermissions=UpgradeImportedPermissions(snapshot.Version, x.GrantedPermissions), Enabled=x.Enabled, Version=x.Version, UpdatedAt=x.UpdatedAt, EmailConfirmed=false, PhoneNumberConfirmed=false, TwoFactorEnabled=false, LockoutEnabled=true }));
        db.PluginCredentials.AddRange(snapshot.Plugins.Select(x => new PluginCredential { Id=x.Id, Name=x.Name, TokenHash=x.TokenHash, Enabled=x.Enabled, CreatedAt=x.CreatedAt, LastSeenAt=x.LastSeenAt }));
        db.ExtensionPolicies.AddRange((snapshot.ExtensionPolicies ?? []).Select(x => new ExtensionPolicy { ExtensionId=x.ExtensionId, Enabled=x.Enabled, AllowNonAdmin=x.AllowNonAdmin, UpdatedAt=x.UpdatedAt }));
        db.UserExtensionPreferences.AddRange((snapshot.ExtensionPreferences ?? []).Select(x => new UserExtensionPreference { UserId=x.UserId, ExtensionId=x.ExtensionId, ShowOnWatch=x.ShowOnWatch, UpdatedAt=x.UpdatedAt }));
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.AccountVersion = snapshot.Metadata.AccountVersion + 1;
        metadata.ForceSenderInTitle = snapshot.Metadata.ForceSenderInTitle;
        metadata.SchedulePullIntervalMinutes = snapshot.Metadata.SchedulePullIntervalMinutes;
        metadata.VisitorAccessEnabled = snapshot.Metadata.VisitorAccessEnabled;
        metadata.AutoEnterVisitorPage = snapshot.Metadata.VisitorAccessEnabled && snapshot.Metadata.AutoEnterVisitorPage;
        var backup = await db.BackupConfigurations.SingleAsync(x => x.Id == 1, ct);
        backup.Enabled=snapshot.Backup.Enabled; backup.Cadence=snapshot.Backup.Cadence; backup.TimeOfDay=snapshot.Backup.TimeOfDay; backup.DayOfWeek=snapshot.Backup.DayOfWeek; backup.MaxBackups=Math.Clamp(snapshot.Backup.MaxBackups,1,100); backup.LastScheduledAt=null; backup.LastSucceededAt=null; backup.LastError=null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (snapshot.Schedule is not null) state.SaveSchedule(snapshot.Schedule);
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
    private static void Validate(ConfigurationSnapshot value) { if(value.Version is not (1 or 2) || value.Roles.Count==0 || value.Users.Count==0) throw new InvalidDataException("Invalid backup schema"); var roleIds=value.Roles.Select(x=>x.Id).ToHashSet(); if(value.Users.Any(x=>!roleIds.Contains(x.RoleId))) throw new InvalidDataException("Unknown role reference"); if(!value.Users.Any(x=>x.Enabled && x.Role==UserRole.Admin)) throw new InvalidDataException("At least one enabled administrator is required"); if(value.Users.Select(x=>x.Username.ToUpperInvariant()).Distinct().Count()!=value.Users.Count) throw new InvalidDataException("Duplicate account ID"); var userIds=value.Users.Select(x=>x.Id).ToHashSet(); if((value.ExtensionPreferences??[]).Any(x=>!userIds.Contains(x.UserId))) throw new InvalidDataException("Unknown extension preference user"); }
}

public sealed record BackupFileInfo(string Name, DateTimeOffset CreatedAt, long Size, string Source);
public sealed record ConfigurationSnapshot(int Version, DateTimeOffset CreatedAt, List<RoleSnapshot> Roles, List<UserSnapshot> Users, List<PluginSnapshot> Plugins, MetadataSnapshot Metadata, BackupSettingsSnapshot Backup, ScheduleBundle? Schedule, List<ExtensionPolicySnapshot>? ExtensionPolicies = null, List<ExtensionPreferenceSnapshot>? ExtensionPreferences = null);
public sealed record RoleSnapshot(Guid Id,string Name,AccountRoleKind Kind,UserPermissions DefaultPermissions,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
public sealed record UserSnapshot(Guid Id,string Username,string NormalizedUsername,string DisplayName,string PasswordHash,string SecurityStamp,string ConcurrencyStamp,UserRole Role,Guid RoleId,UserPermissions GrantedPermissions,bool Enabled,long Version,DateTimeOffset UpdatedAt);
public sealed record PluginSnapshot(Guid Id,string Name,string TokenHash,bool Enabled,DateTimeOffset CreatedAt,DateTimeOffset LastSeenAt);
public sealed record ExtensionPolicySnapshot(string ExtensionId,bool Enabled,bool AllowNonAdmin,DateTimeOffset UpdatedAt);
public sealed record ExtensionPreferenceSnapshot(Guid UserId,string ExtensionId,bool ShowOnWatch,DateTimeOffset UpdatedAt);
public sealed record MetadataSnapshot(
    long AccountVersion,
    bool ForceSenderInTitle,
    int SchedulePullIntervalMinutes,
    bool VisitorAccessEnabled = false,
    bool AutoEnterVisitorPage = false);
public sealed record BackupSettingsSnapshot(bool Enabled,BackupCadence Cadence,TimeSpan TimeOfDay,DayOfWeek DayOfWeek,int MaxBackups);
