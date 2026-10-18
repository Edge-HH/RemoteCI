using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

[AllowAnonymous]
public sealed class LoginSecondFactorModel(UserManager<AppUser> users, SignInManager<AppUser> signIn,
    TwoFactorLoginService challenges, TwoFactorService twoFactor) : PageModel
{
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public bool UseRecoveryCode { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        return await PendingUserAsync() is null ? Expired() : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await PendingUserAsync();
        if (user is null) return Expired();
        var challenge = challenges.Get(HttpContext)!;
        if (!await twoFactor.VerifyAsync(user, Code, UseRecoveryCode, ct))
        {
            Error = await users.IsLockedOutAsync(user) ? "失败次数过多，请稍后重新登录。" : "验证码错误、已过期或已使用，请重试。";
            return Page();
        }
        var reset = await users.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded) return Expired();
        challenges.Clear(HttpContext);
        await signIn.SignInWithClaimsAsync(user, challenge.RememberMe, [new System.Security.Claims.Claim("amr", "mfa")]);
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = Url.IsLocalUrl(challenge.Target) ? challenge.Target : Url.Page("/Account");
        return new EmptyResult();
    }

    public IActionResult OnPostCancel()
    {
        challenges.Clear(HttpContext);
        return RedirectToPage("/Login", new { from = "visitor" });
    }

    private async Task<AppUser?> PendingUserAsync()
    {
        if (challenges.Get(HttpContext) is not { } pending) return null;
        var user = await users.FindByIdAsync(pending.UserId.ToString());
        return user is { Enabled: true, PasswordPending: false, TwoFactorEnabled: true } &&
            user.SecurityStamp == pending.Stamp && !await users.IsLockedOutAsync(user) ? user : null;
    }

    private IActionResult Expired()
    {
        challenges.Clear(HttpContext);
        TempData["Error"] = "登录验证已失效，请重新登录。";
        return RedirectToPage("/Login", new { from = "visitor" });
    }
}
