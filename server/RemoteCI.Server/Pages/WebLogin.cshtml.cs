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
    /// <summary>兑换成功后要进入的本站地址，由落地页在浏览器内跳转。</summary>
    public string Target { get; private set; } = "/";

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
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        Target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl
            : classes.Count > 1 && classId is null ? Url.Page("/ClassSelect")!
            : Url.Page(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account")!;
        // 不能直接 302：链接由手机 App 交给浏览器打开，属于跨站导航，登录 Cookie 是 SameSite=Strict，
        // 跨站导航的重定向请求不会携带它，用户会被重新送回登录页。改为返回本站页面再由页面发起同站跳转。
        return Page();
    }
}
