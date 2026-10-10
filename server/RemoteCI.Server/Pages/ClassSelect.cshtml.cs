using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 登录后的班级选择页：账号可访问多个班级时，先选择进入哪个班级再落地；
/// 进入后仍可使用顶栏的班级切换器随时更换。
/// </summary>
[Authorize]
public sealed class ClassSelectModel(UserManager<AppUser> users) : WebPageModel(users)
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role == UserRole.Admin && AccessibleClasses.Count == 0) return RedirectToPage("/Setup");
        // 单班级（或无班级）账号没有选择的意义，直接走默认落地。
        if (AccessibleClasses.Count <= 1)
            return RedirectToPage(Permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
        return Page();
    }
}
