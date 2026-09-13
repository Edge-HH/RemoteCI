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

        var visitor = await visitorAccess.GetAsync(ct);
        VisitorAccessEnabled = visitor.Enabled;
        if (visitor.Enabled && visitor.AutoEnter && ShouldAutoEnter(from, returnUrl))
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
        VisitorAccessEnabled = (await visitorAccess.GetAsync(ct)).Enabled;
        if (!ModelState.IsValid) return Page();
        var user = await users.FindByNameAsync(Input.Username.Trim());
        if (user is null || !user.Enabled)
        {
            // 恒定时间：与 REST 登录端点同一逻辑，避免响应时间泄露用户名是否存在。
            await identities.EqualizeLoginTimingAsync(Input.Password);
            ModelState.AddModelError(string.Empty, "ID 或密码错误");
            return Page();
        }
        var result = await signIn.PasswordSignInAsync(user, Input.Password, false, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.IsLockedOut ? "登录失败次数过多，请稍后再试" : "ID 或密码错误");
            return Page();
        }
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        return RedirectToPage(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
    }

    public sealed class LoginInput
    {
        [Required, StringLength(32, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string Password { get; set; } = string.Empty;
    }
}
