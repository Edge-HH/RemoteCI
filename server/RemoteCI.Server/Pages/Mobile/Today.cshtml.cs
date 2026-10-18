using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages.Mobile;

[Authorize]
public sealed class TodayModel(UserManager<AppUser> users, IStateStore state, PeerRegistry peers, PersonalScheduleService personal) : WebPageModel(users)
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return Page();
    }
    public async Task<IActionResult> OnGetStateAsync(CancellationToken ct)
    {
        if (await RequireAsync() is not null) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var next = CurrentUser.RoleDefinitionId == AccountRole.TeacherId
            ? await personal.NextAsync(CurrentUser.Id, CurrentUser.DisplayName, DateTimeOffset.UtcNow, ct: ct) : null;
        return new JsonResult(new { snapshot = state.GetLatestSnapshot(CurrentClassId), personal = next,
            online = peers.HasPluginFor(CurrentClassId), className = CurrentClass?.Name, serverTime = DateTimeOffset.UtcNow }, RemoteCI.Shared.JsonDefaults.Options);
    }
}
