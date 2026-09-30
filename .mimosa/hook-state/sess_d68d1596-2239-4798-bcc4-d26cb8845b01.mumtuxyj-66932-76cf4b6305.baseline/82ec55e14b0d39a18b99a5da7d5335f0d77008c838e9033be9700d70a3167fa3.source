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
public sealed class SystemConfigModel(
    UserManager<AppUser> users,
    UpdateService updates,
    IOptions<ServerOptions> serverOptions,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    AppDbContext db,
    ConfigurationArchiveService archives,
    PeerRegistry peers) : WebPageModel(users)
{
    [BindProperty] public UpdateInput UpdateOptions { get; set; } = new();
    [BindProperty] public BackupInput BackupOptions { get; set; } = new();
    [BindProperty] public ExportInput ExportOptions { get; set; } = new();
    [BindProperty] public ImportInput ImportOptions { get; set; } = new();
    public IReadOnlyList<BackupFileInfo> Backups { get; private set; } = [];

    public string CurrentVersion => updates.CurrentVersion;
    public ReleaseInfo? LatestRelease { get; private set; }
    public ReleaseAsset? ServerAsset { get; private set; }
    public string? CheckMessage { get; private set; }
    public bool UpdateSucceeded { get; private set; }
    public bool IsFnos => UpdateService.IsFnosRuntime;
    public bool IsDevelopment => environment.IsDevelopment();
    public UpdateChannel SelectedUpdateChannel =>
        UpdateOptions.Channel == UpdateChannel.Beta ? UpdateChannel.Beta : UpdateChannel.Stable;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBackupAsync();
        if (!UpdateService.CanSelfUpdate(IsDevelopment, IsFnos))
        {
            CheckMessage = IsFnos
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
        if (!UpdateService.CanSelfUpdate(IsDevelopment, IsFnos))
        {
            CheckMessage = IsFnos
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

            var databasePath = Path.IsPathRooted(serverOptions.Value.DatabasePath)
                ? serverOptions.Value.DatabasePath
                : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);
            ServerAsset = updates.SelectServerAsset(LatestRelease)
                ?? throw new InvalidOperationException("未找到当前平台的更新包，请到 GitHub 手动下载。");
            var prepared = await updates.PrepareUpdateAsync(
                LatestRelease, ServerAsset, databasePath, environment.ContentRootPath, ct);
            var installDirectory = UpdateService.ResolveInstallDirectory(AppContext.BaseDirectory);
            var mode = await updates.BeginApplyAsync(prepared, installDirectory, ct);

            CheckMessage = mode == UpdateApplyMode.ExternalInstaller
                ? $"{latestVersion} 更新包已准备，服务端退出后将完成替换并自动重启，请稍后刷新页面。"
                : $"{latestVersion} 更新包已应用，服务端即将重启，请稍后刷新页面。";
            UpdateSucceeded = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                lifetime.StopApplication();
            });
        }
        catch (Exception ex)
        {
            CheckMessage = "更新失败：" + ex.Message;
        }
        return Page();
    }

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
    public sealed class ExportInput { public string Password { get; set; } = string.Empty; public string ConfirmPassword { get; set; } = string.Empty; }
    public sealed class ImportInput { public IFormFile? File { get; set; } public string Password { get; set; } = string.Empty; }

    public sealed class UpdateInput
    {
        public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
        public bool Force { get; set; }
    }
}
