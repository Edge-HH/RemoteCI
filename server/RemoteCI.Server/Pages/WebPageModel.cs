using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

public abstract class WebPageModel(UserManager<AppUser> users) : PageModel
{
    public const string CurrentClassCookie = "RemoteCI.CurrentClass";

    protected UserManager<AppUser> Users { get; } = users;
    public AppUser CurrentUser { get; private set; } = null!;
    public UserPermissions Permissions { get; private set; }

    /// <summary>当前选中的班级（Cookie 优先，否则落到第一个可访问班级）；为 null 表示账号没有任何可访问班级。</summary>
    public ClassSummary? CurrentClass { get; private set; }

    /// <summary>当前班级内的有效权限（按班内成员角色计算；系统管理员为 All）。</summary>
    public UserPermissions ClassPermissions { get; private set; }

    public IReadOnlyList<ClassSummary> AccessibleClasses { get; private set; } = [];

    public Guid CurrentClassId => CurrentClass?.Id ?? Classroom.DefaultId;

    /// <summary>
    /// 加载当前用户并解析班级上下文。permission 针对账号全局权限（如 AccessWebUi/ManageUsers）；
    /// 班级内的操作请用 ClassPermissions 判断。
    /// </summary>
    protected async Task<IActionResult?> RequireAsync(UserPermissions? permission = null)
    {
        var id = Users.GetUserId(User);
        var user = id is not null && Guid.TryParse(id, out var userId)
            ? await Users.Users.Include(x => x.RoleDefinition).SingleOrDefaultAsync(x => x.Id == userId)
            : null;
        if (user is null || !user.Enabled) return RedirectToPage("/Login");
        CurrentUser = user;
        Permissions = RolePermissions.Effective(
            user.Role,
            user.GrantedPermissions,
            user.RoleDefinition.DefaultPermissions);

        var access = HttpContext.RequestServices.GetRequiredService<ClassAccessService>();
        AccessibleClasses = await access.GetAccessibleClassesAsync(
            user.Id, user.Role, user.GrantedPermissions, HttpContext.RequestAborted);
        var cookieClass = Guid.TryParse(Request.Cookies[CurrentClassCookie], out var parsed) ? parsed : (Guid?)null;
        CurrentClass = AccessibleClasses.FirstOrDefault(x => x.Id == cookieClass) ?? AccessibleClasses.FirstOrDefault();
        // 没有任何可访问班级时退回默认班级占位，权限为 None，页面自然呈现无权限状态。
        ClassPermissions = CurrentClass?.Permissions ?? UserPermissions.None;
        return permission is not null && !Permissions.HasFlag(permission.Value)
            ? RedirectToPage("/Denied")
            : null;
    }

    /// <summary>切换当前班级；仅允许切换到可访问的班级，成功后写 Cookie 供后续请求使用。</summary>
    public async Task<IActionResult> OnPostSwitchClassAsync(Guid classId, string? returnUrl)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (AccessibleClasses.Any(x => x.Id == classId))
        {
            Response.Cookies.Append(CurrentClassCookie, classId.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });
        }
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToPage();
    }

    /// <summary>RequireAsync 之后按当前班级的有效权限做检查；班级内操作（通知/课表/控制）使用此方法。</summary>
    protected IActionResult? RequireClass(UserPermissions permission) =>
        ClassPermissions.HasFlag(permission) ? null : RedirectToPage("/Denied");
}
