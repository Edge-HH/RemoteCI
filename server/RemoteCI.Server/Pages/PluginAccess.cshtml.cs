using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 系统设置的“插件与课表同步”tab：为当前班级生成插件配对码（人员管理权限），
/// 查看与吊销插件长期凭据、设置全局自动拉取课表间隔（仅系统管理员）。
/// </summary>
[Authorize]
public sealed class PluginAccessModel(
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    PeerRegistry peers,
    SchedulePullSettings pullSettings) : WebPageModel(users)
{
    [BindProperty] public SchedulePullInterval PullInterval { get; set; }
    public string? PairCode { get; private set; }
    public IReadOnlyList<PluginCredentialInfo> PluginCredentials { get; private set; } = [];
    /// <summary>固定连接码与统一连接码在班级管理中设置，有“班级管理”权限时给出链接。</summary>
    public bool CanManageClasses => HasManagement(ManagementPermissions.ManageClasses);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPairCodeAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        // 配对码绑定当前班级；插件配对后归属该班，只能收发该班的命令与数据。
        if (CurrentClass is null)
        {
            TempData["Error"] = "请先新建班级，再生成配对码。";
            return RedirectToPage();
        }
        PairCode = await identities.CreatePluginPairingCodeAsync(CurrentClassId, ct);
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeCredentialAsync(Guid id, CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        try
        {
            await identities.RevokePluginCredentialAsync(id, ct);
            await peers.DisconnectPluginCredentialAsync(id, ct);
            TempData["Message"] = "插件凭据已吊销，对应插件已断开，需用新配对码重新配对。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(null, null, "credentials");
    }

    public async Task<IActionResult> OnPostPullIntervalAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        // 定时拉取间隔对全部班级生效，随“插件凭据与连接码”系统权限授予。
        if (!Enum.IsDefined(PullInterval))
        {
            TempData["Error"] = "请选择有效的自动拉取间隔。";
            return RedirectToPage(null, null, "schedule-pull");
        }
        await pullSettings.SetIntervalAsync(PullInterval, ct);
        TempData["Message"] = PullInterval == SchedulePullInterval.Disabled
            ? "已关闭定时拉取课表。"
            : "自动拉取课表间隔已保存。";
        return RedirectToPage(null, null, "schedule-pull");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        PluginCredentials = await identities.ListPluginCredentialsAsync(ct);
        PullInterval = await pullSettings.GetIntervalAsync(ct);
    }
}
