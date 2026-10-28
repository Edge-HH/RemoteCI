using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>应用守护：全局默认策略、单设备覆盖与最近守护事件，仅系统管理员可用。</summary>
[Authorize]
public sealed class AppGuardModel(
    UserManager<AppUser> users,
    AppDbContext db,
    AppGuardService appGuard,
    PeerRegistry peers,
    IdentityCoordinator identities) : WebPageModel(users)
{
    private static readonly TimeSpan GuardCommandTimeout = TimeSpan.FromSeconds(120);

    [BindProperty] public PolicyInput Global { get; set; } = new();
    [BindProperty] public PolicyInput Device { get; set; } = new();

    /// <summary>正在编辑覆盖的设备；为空时只显示列表。</summary>
    [BindProperty(SupportsGet = true)] public Guid? Edit { get; set; }

    public string OrganizationName { get; private set; } = AppGuardPolicy.DefaultOrganizationName;
    public IReadOnlyList<DeviceRow> Devices { get; private set; } = [];
    public DeviceRow? Editing { get; private set; }
    public IReadOnlyList<AppGuardEventView> Events { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostGlobalAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await appGuard.SetGlobalAsync(Global.ToPolicy(), ct);
        TempData["Message"] = "全局应用守护策略已保存，并已下发到在线设备";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeviceAsync(Guid credentialId, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await appGuard.SetOverrideAsync(credentialId, Device.ToPolicy(), ct);
            TempData["Message"] = "该设备已改用自定义守护策略";
        }
        catch (KeyNotFoundException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearDeviceAsync(Guid credentialId, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await appGuard.ClearOverrideAsync(credentialId, ct);
            TempData["Message"] = "该设备已恢复为沿用全局策略";
        }
        catch (KeyNotFoundException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public Task<IActionResult> OnPostCloseGuardAsync(Guid credentialId, CancellationToken ct) =>
        ManageGuardAsync(credentialId, uninstall: false, ct);

    public Task<IActionResult> OnPostUninstallGuardAsync(Guid credentialId, CancellationToken ct) =>
        ManageGuardAsync(credentialId, uninstall: true, ct);

    private async Task<IActionResult> ManageGuardAsync(Guid credentialId, bool uninstall, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var device = await appGuard.GetDeviceAsync(credentialId, ct);
        var requestedBy = (await identities.GetProfileAsync(CurrentUser.Id, ct))?.WithPermissions(Permissions);
        if (requestedBy is null) return RedirectToPage("/Login");
        var result = await peers.ManageAppGuardAsync(
            credentialId,
            uninstall ? CommandKind.UninstallAppGuard : CommandKind.StopAppGuard,
            requestedBy,
            GuardCommandTimeout,
            ct);
        if (result.Success)
        {
            var policy = device.Policy;
            policy.Enabled = false;
            if (uninstall) policy.EnhancedMode = false;
            await appGuard.SetOverrideAsync(credentialId, policy, ct);
        }
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var global = await appGuard.GetGlobalAsync(ct);
        Global = PolicyInput.From(global);
        OrganizationName = global.OrganizationName;
        var overrides = await appGuard.GetOverridesAsync(ct);
        var credentials = await db.PluginCredentials.AsNoTracking().Include(x => x.Classroom)
            .Where(x => x.Enabled).ToListAsync(ct);
        Devices = credentials
            .Select(x =>
            {
                var pluginOnline = peers.HasPluginForCredential(x.Id);
                var guardOnline = peers.HasGuardForCredential(x.Id);
                return new DeviceRow(
                    x.Id,
                    DeviceAlertEvaluator.DeviceName(x),
                    x.Classroom?.Name ?? "未分配",
                    pluginOnline,
                    guardOnline,
                    pluginOnline ? peers.PluginForCredentialSupports(x.Id, RemoteCiCapabilities.AppGuard) : null,
                    guardOnline && peers.GuardForCredentialSupports(x.Id, RemoteCiCapabilities.AppGuardManage),
                    overrides.TryGetValue(x.Id, out var policy) ? policy : null,
                    overrides.TryGetValue(x.Id, out var effective) ? effective : global);
            })
            .OrderBy(x => x.ClassName, StringComparer.CurrentCulture)
            .ThenBy(x => x.DeviceName, StringComparer.CurrentCulture)
            .ToList();
        if (Edit is { } editId && Devices.FirstOrDefault(x => x.CredentialId == editId) is { } editing)
        {
            Editing = editing;
            Device = PolicyInput.From(editing.Effective);
        }
        Events = await appGuard.GetRecentEventsAsync(null, 50, ct);
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    /// <param name="Supported">课表插件在线时是否声明了应用守护能力；离线时为 null。</param>
    /// <param name="CanManageGuard">守护在线且声明了远程关闭/卸载能力。</param>
    public sealed record DeviceRow(
        Guid CredentialId,
        string DeviceName,
        string ClassName,
        bool PluginOnline,
        bool GuardOnline,
        bool? Supported,
        bool CanManageGuard,
        AppGuardPolicy? Override,
        AppGuardPolicy Effective);

    public sealed class PolicyInput
    {
        public bool Enabled { get; set; }
        public bool RestartAfterExit { get; set; }
        public int RestartAfterExitDelayMinutes { get; set; }
        public bool RestartOnCrash { get; set; }
        public bool PreventDeletion { get; set; }
        public int BackupIntervalHours { get; set; } = AppGuardPolicy.DefaultBackupIntervalHours;
        public bool EnhancedMode { get; set; }
        public bool RandomBackupPath { get; set; }
        public string BackupDirectoryName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = AppGuardPolicy.DefaultProcessName;
        public string BackupPaths { get; set; } = string.Empty;

        public AppGuardPolicy ToPolicy() => new()
        {
            Enabled = Enabled,
            RestartAfterExit = RestartAfterExit,
            RestartAfterExitDelayMinutes = RestartAfterExitDelayMinutes,
            RestartOnCrash = RestartOnCrash,
            PreventDeletion = PreventDeletion,
            BackupIntervalHours = BackupIntervalHours,
            EnhancedMode = EnhancedMode,
            RandomBackupPath = RandomBackupPath,
            BackupDirectoryName = BackupDirectoryName,
            ProcessName = ProcessName,
            BackupPaths = (BackupPaths ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };

        public static PolicyInput From(AppGuardPolicy policy) => new()
        {
            Enabled = policy.Enabled,
            RestartAfterExit = policy.RestartAfterExit,
            RestartAfterExitDelayMinutes = policy.RestartAfterExitDelayMinutes,
            RestartOnCrash = policy.RestartOnCrash,
            PreventDeletion = policy.PreventDeletion,
            BackupIntervalHours = policy.BackupIntervalHours,
            EnhancedMode = policy.EnhancedMode,
            RandomBackupPath = policy.RandomBackupPath,
            BackupDirectoryName = policy.BackupDirectoryName,
            ProcessName = policy.ProcessName,
            BackupPaths = string.Join(Environment.NewLine, policy.BackupPaths),
        };
    }
}
