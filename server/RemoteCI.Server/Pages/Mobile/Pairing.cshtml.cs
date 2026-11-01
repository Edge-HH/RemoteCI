using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class PairingModel(UserManager<AppUser> users, IdentityCoordinator identities, PeerRegistry peers) : WebPageModel(users)
{
    public IReadOnlyList<PluginCredentialInfo> Credentials { get; private set; } = [];
    public string? Code { get; private set; }
    [BindProperty] public string Mode { get; set; } = "once";
    [BindProperty] public string? RequestedCode { get; set; }
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        Credentials = await identities.ListPluginCredentialsAsync(ct); return Page();
    }
    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        if (Mode != "shared" && CurrentClass is null) { TempData["Error"] = "请先选择班级"; return RedirectToPage(); }
        try
        {
            Code = Mode switch
            {
                "shared" => await identities.CreateSharedPluginPairingCodeAsync(RequestedCode, ct),
                "persistent" => await identities.SetClassPairingCodeAsync(CurrentClassId, RequestedCode, ct),
                _ => await identities.CreatePluginPairingCodeAsync(CurrentClassId, ct),
            };
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; return RedirectToPage(); }
        Credentials = await identities.ListPluginCredentialsAsync(ct); return Page();
    }
    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManagePluginAccess) is { } denied) return denied;
        try { await identities.RevokePluginCredentialAsync(id, ct); await peers.DisconnectPluginCredentialAsync(id, ct); TempData["Message"] = "凭据已吊销，插件需重新配对"; }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }
}
