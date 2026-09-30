using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class NotificationsModel(UserManager<AppUser> users) : WebPageModel(users)
{
    // 保留旧地址兼容书签，通知功能的唯一实现位于控制页。
    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        return LocalRedirect("/Control#send-notification");
    }
}
