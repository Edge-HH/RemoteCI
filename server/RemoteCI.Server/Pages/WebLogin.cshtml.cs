using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 客户端一键打开 WebUI 的落地页：兑换 /api/auth/web-ticket 签发的一次性票据，
/// 以票据所属账号登录 WebUI（替换浏览器里原有的登录），再跳到客户端指定的本站页面。
/// </summary>
public sealed class WebLoginModel(
    SignInManager<AppUser> signIn,
    IdentityCoordinator identities,
    ClassAccessService access) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? t, string? returnUrl, Guid? classId, CancellationToken ct)
    {
        // 票据出现在地址栏中：禁止缓存与 Referer 外泄，兑换后立即重定向离开。
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var user = await identities.RedeemWebLoginTicketAsync(t, ct);
        if (user is null) return RedirectToPage("/Login", new { from = "visitor" });

        await signIn.SignOutAsync();
        await signIn.SignInAsync(user, isPersistent: false);

        var classes = await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct);
        // 沿用客户端当前选中的班级，让 WebUI 打开后与手机上看到的是同一个班。
        if (classId is { } selected && classes.Any(x => x.Id == selected))
        {
            Response.Cookies.Append(WebPageModel.CurrentClassCookie, selected.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });
        }
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        if (classes.Count > 1 && classId is null) return RedirectToPage("/ClassSelect");
        return RedirectToPage(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
    }
}
