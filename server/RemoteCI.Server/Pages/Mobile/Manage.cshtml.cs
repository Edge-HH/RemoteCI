using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class ManageModel(UserManager<AppUser> users) : WebPageModel(users)
{
    public async Task<IActionResult> OnGetAsync() => await RequireAsync(UserPermissions.ManageUsers) ?? Page();
}
