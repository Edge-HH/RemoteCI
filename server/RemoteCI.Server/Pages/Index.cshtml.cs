using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class IndexModel(
    UserManager<AppUser> users, PeerRegistry peers, IStateStore state, IdentityCoordinator identities, MobileLoginSettings mobileLogin)
    : WebPageModel(users)
{
    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    public ClassStateSnapshot? Snapshot { get; private set; }
    public ScheduleBundle? Schedule { get; private set; }
    public string? PairCode { get; private set; }
    public string MobileLoginUrl { get; private set; } = string.Empty;
    public IReadOnlyList<PeerCapabilityDiagnostic> CapabilityDiagnostics { get; private set; } = [];
    public PluginProtocolMismatch? PluginProtocolMismatch => peers.LatestPluginProtocolMismatch;
    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;
    public string ServerVersion => AppVersion.Version;
    public bool Supports(string capability) => !PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, capability);
    public string FormatCapabilities(IReadOnlyList<string> capabilities) => string.Join(
        "、", capabilities.Select(capability => $"{capability}（{RemoteCiCapabilities.ChineseName(capability)}）"));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        // 系统管理员首次进入、还没有任何班级时，先到初始化向导新建第一个班级。
        if (IsAdmin && AccessibleClasses.Count == 0) return RedirectToPage("/Setup");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRetryConnectionAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        // 面向当前班级的连接检测：定向重新下发该班的授权镜像。
        var sync = await identities.CreateSyncAsync(CurrentClassId, ct);
        var connected = await peers.SendToPluginAsync(CurrentClassId, Envelope.AccountSync(sync), ct);
        TempData[connected ? "Message" : "Error"] = connected
            ? "插件连接检测成功，账号与权限已重新同步。"
            : "插件仍未连接，ClassIsland 插件会每 5 秒自动重试，请检查插件设置与服务地址。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPairCodeAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (!HasManagement(ManagementPermissions.ManagePluginAccess)) return RedirectToPage("/Denied");
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

    /// <summary>按需生成手机扫码登录二维码，见 <see cref="MobileLoginQr"/>。</summary>
    public async Task<IActionResult> OnPostMobileLoginQrAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        return await MobileLoginQr.CreateAsync(identities, mobileLogin, Request, CurrentUser.Id, ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        MobileLoginUrl = await MobileLoginQr.ResolveServerUrlAsync(mobileLogin, Request, ct);
        Snapshot = state.GetLatestSnapshot(CurrentClassId);
        Schedule = state.GetLatestSchedule(CurrentClassId);
        if (CurrentUser.Role == UserRole.Admin)
            CapabilityDiagnostics = peers.GetCapabilityDiagnostics(CurrentClassId);
    }
}
