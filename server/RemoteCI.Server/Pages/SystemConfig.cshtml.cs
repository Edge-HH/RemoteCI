using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

[Authorize]
// 手动上传的服务端更新包远超 Kestrel 默认 30MB 请求体上限；整页仅管理员可用。
[RequestSizeLimit(UpdateService.MaxUploadedPackageBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = UpdateService.MaxUploadedPackageBytes)]
public sealed class SystemConfigModel(
    UserManager<AppUser> users,
    UpdateService updates,
    IOptions<ServerOptions> serverOptions,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    AppDbContext db,
    ConfigurationArchiveService archives,
    PeerRegistry peers,
    IdentityCoordinator identities,
    OperationsSettings operations,
    AppGuardService appGuard) : WebPageModel(users)
{
    [BindProperty] public OperationsInput OperationsOptions { get; set; } = new();
    [BindProperty] public UpdateInput UpdateOptions { get; set; } = new();
    [BindProperty] public BackupInput BackupOptions { get; set; } = new();
    [BindProperty] public ExportInput ExportOptions { get; set; } = new();
    [BindProperty] public ImportInput ImportOptions { get; set; } = new();
    [BindProperty] public bool ForceSenderInTitle { get; set; }
    [BindProperty, StringLength(OrganizationNameMaxLength)] public string? OrganizationName { get; set; }
    public const int OrganizationNameMaxLength = 60;
    [BindProperty] public IFormFile? UpdatePackage { get; set; }
    public IReadOnlyList<BackupFileInfo> Backups { get; private set; } = [];
    public IReadOnlyList<string> UnreadableTwoFactorUsers { get; private set; } = [];

    public string CurrentVersion => updates.CurrentVersion;
    public ReleaseInfo? LatestRelease { get; private set; }
    public ReleaseAsset? ServerAsset { get; private set; }
    public string? CheckMessage { get; private set; }
    public bool UpdateSucceeded { get; private set; }
    public bool IsFnos => UpdateService.IsFnosRuntime;
    /// <summary>没有容器入口脚本的旧 fnOS 镜像：只能在应用中心安装新版 FPK。</summary>
    public bool IsPlatformManaged => UpdateService.CurrentApplyMode == UpdateApplyMode.ManagedByPlatform;
    public bool UsesPersistentOverlay => UpdateService.CurrentApplyMode == UpdateApplyMode.PersistentOverlay;
    public bool IsDevelopment => environment.IsDevelopment();
    public string CurrentPlatform => UpdateService.CurrentPlatform;
    public UpdateChannel SelectedUpdateChannel =>
        UpdateOptions.Channel == UpdateChannel.Beta ? UpdateChannel.Beta : UpdateChannel.Stable;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        ForceSenderInTitle = await identities.GetForceSenderInTitleAsync();
        OrganizationName = await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => x.OrganizationName).SingleAsync();
        OperationsOptions = OperationsInput.From(await operations.GetAsync());
        return Page();
    }

    public async Task<IActionResult> OnPostSaveOperationsSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await operations.SetAsync(OperationsOptions.ToValues(), ct);
            TempData["Message"] = "集控运维设置已保存";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(null, null, "device-alerts");
    }

    public async Task<IActionResult> OnPostSaveOrganizationAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var name = string.IsNullOrWhiteSpace(OrganizationName) ? null : OrganizationName.Trim();
        if (name is { Length: > OrganizationNameMaxLength })
        {
            TempData["Error"] = $"组织名不能超过 {OrganizationNameMaxLength} 个字符";
            return RedirectToPage(null, null, "organization");
        }
        var row = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        row.OrganizationName = name;
        await db.SaveChangesAsync(ct);
        await appGuard.OrganizationNameChangedAsync(ct);
        TempData["Message"] = name is null ? "已清除组织名" : "组织名已保存";
        return RedirectToPage(null, null, "organization");
    }

    public async Task<IActionResult> OnPostSaveNotificationSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var settings = await identities.SetForceSenderInTitleAsync(ForceSenderInTitle, ct);
        await peers.SendSettingsToWatchesAsync(settings, ct);
        TempData["Message"] = ForceSenderInTitle ? "已开启强制显示发送人" : "已关闭强制显示发送人";
        return RedirectToPage(null, null, "notification-sender");
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        if (!UpdateService.CanSelfUpdateNow(IsDevelopment))
        {
            CheckMessage = IsPlatformManaged
                ? UpdateService.FnosManagedMessage
                : UpdateService.DevelopmentManagedMessage;
            return Page();
        }
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(SelectedUpdateChannel, ct);
            if (LatestRelease is null)
            {
                CheckMessage = "仓库暂无 release。";
                return Page();
            }
            var latestVersion = UpdateService.NormalizeVersion(LatestRelease.Tag);
            ServerAsset = updates.SelectServerAsset(LatestRelease);
            var comparison = UpdateService.CompareVersions(latestVersion, CurrentVersion);
            CheckMessage = comparison switch
            {
                > 0 => $"发现新版本 {latestVersion}。",
                0 when UpdateOptions.Force => $"当前已是 {CurrentVersion}，可强制重新下载并覆盖安装。",
                0 => $"当前已是最新版本 {CurrentVersion}。",
                _ => $"所选渠道最新版本 {latestVersion} 低于当前版本，拒绝降级。",
            };
        }
        catch (Exception ex)
        {
            CheckMessage = "检查更新失败：" + ex.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        if (!UpdateService.CanSelfUpdateNow(IsDevelopment))
        {
            CheckMessage = IsPlatformManaged
                ? UpdateService.FnosManagedMessage
                : UpdateService.DevelopmentManagedMessage;
            return Page();
        }
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(SelectedUpdateChannel, ct)
                ?? throw new InvalidOperationException("仓库暂无 release，无法更新。");
            var latestVersion = UpdateService.NormalizeVersion(LatestRelease.Tag);
            if (!UpdateService.CanInstall(latestVersion, CurrentVersion, UpdateOptions.Force))
                throw new InvalidOperationException(
                    UpdateService.CompareVersions(latestVersion, CurrentVersion) < 0
                        ? "当前版本高于所选渠道的最新版本，拒绝降级。"
                        : "已是最新版本；如需覆盖安装，请启用强制更新。");

            ServerAsset = updates.SelectServerAsset(LatestRelease)
                ?? throw new InvalidOperationException("未找到当前平台的更新包，请到 GitHub 手动下载。");
            var prepared = await updates.PrepareUpdateAsync(
                LatestRelease, ServerAsset, ResolveDatabasePath(), environment.ContentRootPath, ct);
            await ApplyAndRestartAsync(prepared, latestVersion, ct);
        }
        catch (Exception ex)
        {
            CheckMessage = "更新失败：" + ex.Message;
        }
        return Page();
    }

    /// <summary>调试用：安装管理员上传的服务端包，允许同版本覆盖与降级。</summary>
    public async Task<IActionResult> OnPostUploadUpdateAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        if (!UpdateService.CanSelfUpdateNow(IsDevelopment))
        {
            CheckMessage = IsPlatformManaged
                ? UpdateService.FnosManagedMessage
                : UpdateService.DevelopmentManagedMessage;
            return Page();
        }
        if (UpdatePackage is null || UpdatePackage.Length == 0)
        {
            CheckMessage = "请选择服务端更新包（.zip）。";
            return Page();
        }
        try
        {
            await using var stream = UpdatePackage.OpenReadStream();
            var (prepared, version) = await updates.PrepareUploadedUpdateAsync(
                stream, ResolveDatabasePath(), environment.ContentRootPath, ct);
            await ApplyAndRestartAsync(prepared, version, ct);
        }
        catch (Exception ex)
        {
            CheckMessage = "手动更新失败：" + ex.Message;
        }
        return Page();
    }

    private async Task ApplyAndRestartAsync(PreparedUpdate prepared, string version, CancellationToken ct)
    {
        var installDirectory = UpdateService.ResolveInstallDirectory(AppContext.BaseDirectory);
        var mode = await updates.BeginApplyAsync(prepared, installDirectory, ct);
        CheckMessage = mode == UpdateApplyMode.ExternalInstaller
            ? $"{version} 更新包已准备，服务端退出后将完成替换并自动重启，请稍后刷新页面。"
            : $"{version} 更新包已应用，服务端即将重启，请稍后刷新页面。";
        UpdateSucceeded = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            lifetime.StopApplication();
        });
    }

    private string ResolveDatabasePath() =>
        Path.IsPathRooted(serverOptions.Value.DatabasePath)
            ? serverOptions.Value.DatabasePath
            : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);

    public async Task<IActionResult> OnPostSaveBackupSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var settings = await db.BackupConfigurations.SingleAsync(x => x.Id == 1, ct);
        settings.Enabled = BackupOptions.Enabled;
        settings.Cadence = BackupOptions.Cadence;
        settings.TimeOfDay = BackupOptions.TimeOfDay;
        settings.DayOfWeek = BackupOptions.DayOfWeek;
        settings.MaxBackups = Math.Clamp(BackupOptions.MaxBackups, 1, 100);
        settings.LastScheduledAt = null;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Backup settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBackupNowAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try { await archives.CreateLocalBackupAsync("manual", ct); TempData["Message"] = "Backup created."; }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetDownloadBackupAsync(string name)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try { return File(archives.ReadBackup(name), "application/octet-stream", name); }
        catch { return NotFound(); }
    }

    public async Task<IActionResult> OnPostDeleteBackupAsync(string name)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try { archives.DeleteBackup(name); TempData["Message"] = "Backup deleted."; }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreBackupAsync(string name, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await archives.CreateLocalBackupAsync("preimport", ct);
            await archives.ApplyAsync(archives.ParseLocalBackup(archives.ReadBackup(name)), ct);
            await peers.DisconnectAllAsync(ct);
            ScheduleRestart();
            TempData["Message"] = "Configuration restored; server is restarting.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExportConfigAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (ExportOptions.Password != ExportOptions.ConfirmPassword || ExportOptions.Password.Length < 8)
        { TempData["Error"] = "Export passwords must match and contain at least 8 characters."; return RedirectToPage(); }
        try { return File(await archives.ExportEncryptedAsync(ExportOptions.Password, ct), "application/octet-stream", $"remoteci-config-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.rcicfg"); }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToPage(); }
    }

    public async Task<IActionResult> OnPostImportConfigAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (ImportOptions.File is null || ImportOptions.File.Length > 64 * 1024 * 1024) { TempData["Error"] = "Choose a valid configuration package."; return RedirectToPage(); }
        try
        {
            using var memory = new MemoryStream(); await ImportOptions.File.CopyToAsync(memory, ct);
            var snapshot = archives.ReadEncrypted(memory.ToArray(), ImportOptions.Password);
            await archives.CreateLocalBackupAsync("preimport", ct);
            await archives.ApplyAsync(snapshot, ct);
            await peers.DisconnectAllAsync(ct);
            ScheduleRestart();
            TempData["Message"] = "Configuration imported; server is restarting.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    private async Task LoadBackupAsync()
    {
        var value = await db.BackupConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1);
        BackupOptions = new BackupInput { Enabled=value.Enabled, Cadence=value.Cadence, TimeOfDay=value.TimeOfDay, DayOfWeek=value.DayOfWeek, MaxBackups=value.MaxBackups };
        Backups = archives.ListBackups();
        UnreadableTwoFactorUsers = await archives.FindUnreadableTwoFactorUsersAsync(HttpContext.RequestAborted);
    }

    private void ScheduleRestart() => ApplicationRestartCoordinator.ScheduleRestart(lifetime, environment);

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    public sealed class BackupInput
    {
        public bool Enabled { get; set; }
        public BackupCadence Cadence { get; set; } = BackupCadence.Daily;
        public TimeSpan TimeOfDay { get; set; } = TimeSpan.FromHours(2);
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
        [Range(1, 100)] public int MaxBackups { get; set; } = 7;
    }
    public sealed class OperationsInput
    {
        [Range(CommandAuditRetention.Min, CommandAuditRetention.Max)] public int AuditRetentionDays { get; set; } = CommandAuditRetention.Default;
        public bool DeviceAlertsEnabled { get; set; } = true;
        [Range(DeviceAlertDefaults.MinThresholdMinutes, DeviceAlertDefaults.MaxThresholdMinutes)]
        public int DeviceAlertThresholdMinutes { get; set; } = DeviceAlertDefaults.ThresholdMinutes;
        public bool DeviceAlertNotifyAdmins { get; set; } = true;
        public bool DeviceAlertNotifyClassHeads { get; set; } = true;
        public bool DeviceAlertSchoolHoursOnly { get; set; } = true;
        public bool ProfileDriftCheckEnabled { get; set; } = true;
        public string ProfileDriftCheckTime { get; set; } = "12:30";

        public static OperationsInput From(OperationsSettingsValues values) => new()
        {
            AuditRetentionDays = values.AuditRetentionDays,
            DeviceAlertsEnabled = values.DeviceAlertsEnabled,
            DeviceAlertThresholdMinutes = values.DeviceAlertThresholdMinutes,
            DeviceAlertNotifyAdmins = values.DeviceAlertNotifyAdmins,
            DeviceAlertNotifyClassHeads = values.DeviceAlertNotifyClassHeads,
            DeviceAlertSchoolHoursOnly = values.DeviceAlertSchoolHoursOnly,
            ProfileDriftCheckEnabled = values.ProfileDriftCheckEnabled,
            ProfileDriftCheckTime = values.ProfileDriftCheckTime,
        };

        public OperationsSettingsValues ToValues() => new(
            AuditRetentionDays, DeviceAlertsEnabled, DeviceAlertThresholdMinutes,
            DeviceAlertNotifyAdmins, DeviceAlertNotifyClassHeads, DeviceAlertSchoolHoursOnly,
            ProfileDriftCheckEnabled, ProfileDriftCheckTime);
    }
    public sealed class ExportInput { public string Password { get; set; } = string.Empty; public string ConfirmPassword { get; set; } = string.Empty; }
    public sealed class ImportInput { public IFormFile? File { get; set; } public string Password { get; set; } = string.Empty; }

    public sealed class UpdateInput
    {
        public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
        public bool Force { get; set; }
    }
}
