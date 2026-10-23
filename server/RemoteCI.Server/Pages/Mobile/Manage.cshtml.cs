using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class ManageModel(UserManager<AppUser> users) : WebPageModel(users)
{
    // 管理项已并入“我的”页；旧链接按原权限检查后跳过去。
    public async Task<IActionResult> OnGetAsync() => await RequireAsync(UserPermissions.ManageUsers) ?? Redirect("/Mobile/Account#manage");
}
