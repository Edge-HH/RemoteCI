using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class TwoFactorModel(UserManager<AppUser> users, SignInManager<AppUser> signIn,
    TwoFactorService twoFactor, AuthorizationSyncService authorizationSync) : WebPageModel(users)
{
    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? SetupToken { get; set; }
    [BindProperty] public bool UseRecoveryCode { get; set; }
    public string? SharedKey { get; private set; }
    public string? QrSvg { get; private set; }
    public string? Error { get; private set; }
    public string[]? RecoveryCodes { get; private set; }
    public int RecoveryCodesRemaining => (CurrentUser.TwoFactorRecoveryHashes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        return await RequireAsync() ?? Page();
    }

    public async Task<IActionResult> OnPostBeginAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (await RequireAsync() is { } denied) return denied;
        ModelState.Clear();
        try
        {
            await twoFactor.CheckPasswordAsync(CurrentUser, CurrentPassword ?? "");
            SetupToken = twoFactor.BeginSetup(CurrentUser);
            ShowSetup();
        }
        catch (IdentityOperationException ex) { Error = ex.Message; }
        CurrentPassword = null;
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (await RequireAsync() is { } denied) return denied;
        ModelState.Clear();
        try
        {
            RecoveryCodes = await twoFactor.EnableAsync(CurrentUser, SetupToken ?? "", Code ?? "", ct);
            await authorizationSync.SyncAsync(ct);
            await signIn.RefreshSignInAsync(CurrentUser);
            SetupToken = null;
        }
        catch (IdentityOperationException ex)
        {
            Error = ex.Message;
            try { ShowSetup(); }
            catch (IdentityOperationException) { SetupToken = null; }
        }
        Code = null;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (await RequireAsync() is { } denied) return denied;
        ModelState.Clear();
        try
        {
            await twoFactor.DisableAsync(CurrentUser, CurrentPassword ?? "", Code ?? "", UseRecoveryCode, ct);
            await authorizationSync.SyncAsync(ct);
            await signIn.RefreshSignInAsync(CurrentUser);
            TempData["Message"] = "二步验证已关闭，原认证器和恢复码已失效。";
            return RedirectToPage();
        }
        catch (IdentityOperationException ex) { Error = ex.Message; }
        CurrentPassword = null;
        Code = null;
        return Page();
    }

    private void ShowSetup()
    {
        var setup = twoFactor.ReadSetup(CurrentUser, SetupToken ?? "");
        SharedKey = setup.Secret;
        var label = Uri.EscapeDataString($"RemoteCI:{CurrentUser.UserName}");
        var payload = $"otpauth://totp/{label}?secret={setup.Secret}&issuer=RemoteCI&algorithm=SHA1&digits=6&period=30";
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var renderer = new SvgQRCode(data);
        QrSvg = renderer.GetGraphic(4);
    }
}
