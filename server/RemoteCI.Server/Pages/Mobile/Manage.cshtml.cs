using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class ManageModel(UserManager<AppUser> users) : WebPageModel(users)
{
    // 管理项已并入“我的”页；旧链接在账号至少有一个管理项时跳过去，各管理项在那里按各自权限显示。
    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        var hasManageItem = CurrentUser.Role == UserRole.Admin || Permissions.HasFlag(UserPermissions.ManageUsers) ||
            HasManagement(ManagementPermissions.ManageRoles) || HasManagement(ManagementPermissions.ManageLoginPage) ||
            HasManagement(ManagementPermissions.ManagePluginAccess);
        return hasManageItem ? Redirect("/Mobile/Account#manage") : RedirectToPage("/Denied");
    }
}
