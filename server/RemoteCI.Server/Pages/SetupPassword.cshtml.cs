using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 首次登录的密码设置页：批量导入且未设密码的账号，凭空密码登录时下发的一次性令牌在此补设密码。
/// </summary>
[AllowAnonymous]
public sealed class SetupPasswordModel(IdentityCoordinator identities) : PageModel
{
    [BindProperty]
    public string SetupToken { get; set; } = string.Empty;

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请输入新密码。")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "密码需为 8-128 个字符")]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请再次输入新密码。")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public IActionResult OnGet(string? t, string? u)
    {
        if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(u))
            return RedirectToPage("/Login");
        SetupToken = t;
        Username = u;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(string.Empty, "两次输入的新密码不一致。");
            return Page();
        }
        try
        {
            await identities.SetupPasswordAsync(new SetupPasswordRequest
            {
                Username = Username,
                SetupToken = SetupToken,
                NewPassword = NewPassword,
            }, ct);
        }
        catch (IdentityOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        TempData["Message"] = "密码已设置，请使用新密码登录。";
        return RedirectToPage("/Login");
    }
}
