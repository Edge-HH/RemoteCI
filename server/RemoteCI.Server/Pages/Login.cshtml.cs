using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

public sealed class LoginModel(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    IdentityCoordinator identities,
    VisitorAccessSettings visitorAccess) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();
    public bool VisitorAccessEnabled { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? from, string? returnUrl, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } user)
        {
            var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
            return RedirectToPage(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
        }

        var visitorEnabled = await visitorAccess.AnyVisitorClassEnabledAsync(ct);
        var autoEnter = visitorEnabled && await visitorAccess.GetAutoEnterAsync(ct);
        VisitorAccessEnabled = visitorEnabled;
        if (autoEnter && ShouldAutoEnter(from, returnUrl))
            return RedirectToPage("/Visitor");
        return Page();
    }

    internal static bool ShouldAutoEnter(string? from, string? returnUrl)
    {
        if (string.Equals(from, "visitor", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(returnUrl))
            return true;
        var path = returnUrl.Split('?', 2)[0].TrimEnd('/');
        return path.Length == 0 || path.Equals("/Index", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        VisitorAccessEnabled = await visitorAccess.AnyVisitorClassEnabledAsync(ct);
        if (!ModelState.IsValid) return Page();
        var user = await users.FindByNameAsync(Input.Username.Trim());
        if (user is null || !user.Enabled)
        {
            // 恒定时间：与 REST 登录端点同一逻辑，避免响应时间泄露用户名是否存在。
            await identities.EqualizeLoginTimingAsync(Input.Password);
            ModelState.AddModelError(string.Empty, "ID 或密码错误");
            return Page();
        }
        if (user.PasswordPending && string.IsNullOrEmpty(Input.Password))
        {
            // 首次登录的待激活账号：生成一次性令牌并进入“设置密码”页。
            var setup = await identities.BeginPasswordSetupAsync(user, ct);
            var url = $"/SetupPassword?t={Uri.EscapeDataString(setup.SetupToken!)}&u={Uri.EscapeDataString(user.UserName!)}";
            Response.StatusCode = StatusCodes.Status303SeeOther;
            Response.Headers.Location = url;
            return new EmptyResult();
        }
        if (user.PasswordPending)
        {
            ModelState.AddModelError(string.Empty, "该账号尚未设置密码，请留空密码登录以完成首次设置");
            return Page();
        }
        var result = await signIn.PasswordSignInAsync(user, Input.Password, Input.RememberMe, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.IsLockedOut ? "登录失败次数过多，请稍后再试" : "ID 或密码错误");
            return Page();
        }
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        // 多班级账号登录后先选择进入的班级；单班级账号保持原有落地逻辑。
        var access = HttpContext.RequestServices.GetRequiredService<ClassAccessService>();
        var accessibleClasses = await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct);
        string landing;
        if (accessibleClasses.Count > 1)
        {
            landing = "/ClassSelect";
        }
        else
        {
            landing = permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account";
        }
        // 登录表单是 POST。使用 303 明确要求浏览器以 GET 打开落地页，避免
        // 用户返回时恢复 POST 历史并触发“重新提交表单”（ERR_CACHE_MISS）。
        return RedirectAfterPost(landing);
    }

    private IActionResult RedirectAfterPost(string page)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = Url.Page(page) ?? "/";
        return new EmptyResult();
    }

    public sealed class LoginInput
    {
        [Required, StringLength(32, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        // 密码可留空：批量导入的待激活账号首次登录时以此为入口设置密码。
        [StringLength(128)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
