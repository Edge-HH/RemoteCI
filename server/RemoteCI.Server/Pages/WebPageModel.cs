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
    /// <summary>按全局角色计算的有效权限（全局入口如 AccessWebUi/ManageUsers 使用）。</summary>
    public UserPermissions Permissions { get; private set; }

    /// <summary>系统管理权限（全局，不随班级变化）；系统管理员为全部。</summary>
    public ManagementPermissions Management { get; private set; }

    /// <summary>是否拥有某项系统管理权限；系统管理员始终拥有。</summary>
    public bool HasManagement(ManagementPermissions permission) => Management.HasFlag(permission);

    /// <summary>当前操作者修改他人或角色权限时的授权边界：非管理员只能授予自己拥有的权限。</summary>
    public PermissionDelegation Delegation => new(CurrentUser.Role, Permissions, Management);

    /// <summary>当前选中的班级（Cookie 优先，否则落到第一个可访问班级）；为 null 表示账号没有任何可访问班级。</summary>
    public ClassSummary? CurrentClass { get; private set; }

    /// <summary>当前班级内的有效权限（按班内成员角色计算；系统管理员为 All）。</summary>
    public UserPermissions ClassPermissions { get; private set; }

    public IReadOnlyList<ClassSummary> AccessibleClasses { get; private set; } = [];

    /// <summary>
    /// 当前用户在当前班级可自行完成的班级管理操作，由本班的“班级名称与头像”“从教室刷新课表”权限决定；
    /// 系统管理员全部允许。
    /// </summary>
    public ClassSelfServicePolicy ClassSelfService { get; private set; } = ClassSelfServicePolicy.None;

    /// <summary>当前用户是否可以修改当前班级的班名或班头像（任一项被允许即显示“班级设置”入口）。</summary>
    public bool CanManageClassInfo => ClassSelfService.CanRename || ClassSelfService.CanChangeAvatar;

    /// <summary>当前班级 Id；账号没有任何可访问班级时为 Guid.Empty，不会命中任何班级数据。</summary>
    public Guid CurrentClassId => CurrentClass?.Id ?? Guid.Empty;

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
        Permissions = PermissionAdjustment.Of(user).Apply(user.Role, user.RoleDefinition.DefaultPermissions);
        Management = RolePermissions.EffectiveManagement(user.Role, user.RoleDefinition.DefaultManagementPermissions,
            user.GrantedManagementPermissions, user.RevokedManagementPermissions);

        var access = HttpContext.RequestServices.GetRequiredService<ClassAccessService>();
        try
        {
            AccessibleClasses = await access.GetAccessibleClassesAsync(
                user.Id, user.Role, HttpContext.RequestAborted);
            var cookieClass = Guid.TryParse(Request.Cookies[CurrentClassCookie], out var parsed) ? parsed : (Guid?)null;
            CurrentClass = AccessibleClasses.FirstOrDefault(x => x.Id == cookieClass) ?? AccessibleClasses.FirstOrDefault();
            // 没有任何可访问班级时班级 Id 为空、权限为 None，页面自然呈现无权限状态。
            ClassPermissions = CurrentClass?.Permissions ?? UserPermissions.None;
            ClassSelfService = await access.GetClassSelfServiceAsync(user.Id, user.Role, CurrentClassId, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 请求已被客户端/代理中止：视为正常取消，停止后续处理并避免记录为错误
            return new EmptyResult();
        }
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
            var browser = Request.Cookies[MobileUi.BrowserCookie];
            if (browser is not null)
            {
                var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                await db.WebPushSubscriptions.Where(x => x.UserId == CurrentUser.Id && x.BrowserId == browser && (x.ClassId != null || x.EventMask != 0))
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.ClassId, classId), HttpContext.RequestAborted);
            }
        }
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToPage();
    }

    /// <summary>RequireAsync 之后按当前班级的有效权限做检查；班级内操作（通知/课表/控制）使用此方法。</summary>
    protected IActionResult? RequireClass(UserPermissions permission) =>
        ClassPermissions.HasFlag(permission) ? null : RedirectToPage("/Denied");

    /// <summary>加载当前用户并要求某项系统管理权限（系统管理员始终通过）；系统级页面使用此方法。</summary>
    protected async Task<IActionResult?> RequireManagementAsync(ManagementPermissions permission)
    {
        if (await RequireAsync() is { } denied) return denied;
        return HasManagement(permission) ? null : RedirectToPage("/Denied");
    }
}
