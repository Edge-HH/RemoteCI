using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>系统管理员维护登录页外观：主题策略、背景图、背景不透明度与卡片位置。</summary>
[Authorize]
public sealed class LoginSettingsModel(
    UserManager<AppUser> users,
    LoginPageSettings settings) : WebPageModel(users)
{
    [BindProperty] public AppearanceInput Appearance { get; set; } = new();
    [BindProperty] public IFormFile? BackgroundFile { get; set; }

    public bool HasBackground { get; private set; }
    public DateTimeOffset? BackgroundUpdatedAt { get; private set; }
    public int MaxBackgroundKb => LoginPageSettings.MaxBackgroundBytes / 1024;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "设置值无效，请重新选择。";
            await LoadAsync(ct);
            return Page();
        }

        // 仅在确实选择了文件时覆盖背景图；留空表示保留现有背景。
        if (BackgroundFile is { Length: > 0 })
        {
            if (BackgroundFile.Length > LoginPageSettings.MaxBackgroundBytes)
            {
                TempData["Error"] = $"背景图需不超过 {MaxBackgroundKb}KB。";
                return RedirectToPage();
            }
            if (!LoginPageSettings.AllowedBackgroundContentTypes.Contains(BackgroundFile.ContentType))
            {
                TempData["Error"] = "背景图仅支持 PNG/JPEG/WebP。";
                return RedirectToPage();
            }
            using var memory = new MemoryStream();
            await BackgroundFile.CopyToAsync(memory, ct);
            await settings.SetBackgroundAsync(memory.ToArray(), BackgroundFile.ContentType, ct);
        }

        await settings.SetAppearanceAsync(Appearance.Theme, Appearance.Opacity, Appearance.CardPosition, ct);
        TempData["Message"] = "登录页设置已保存。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveBackgroundAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await settings.SetBackgroundAsync(null, null, ct);
        TempData["Message"] = "已移除登录页背景图。";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        var appearance = await settings.GetAppearanceAsync(ct);
        Appearance = new AppearanceInput { Theme = appearance.Theme, Opacity = appearance.Opacity, CardPosition = appearance.CardPosition };
        HasBackground = appearance.HasBackground;
        BackgroundUpdatedAt = appearance.BackgroundUpdatedAt;
    }

    /// <summary>登录页设置属于系统级配置，只允许系统管理员访问。</summary>
    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    public sealed class AppearanceInput
    {
        /// <summary>主题策略；未选择时跟随访客本地偏好。</summary>
        public LoginTheme Theme { get; set; } = LoginTheme.Follow;

        /// <summary>背景图不透明度百分比。</summary>
        [Range(0, 100)] public int Opacity { get; set; } = 100;

        /// <summary>登录卡片在页面中的水平位置；默认居中。</summary>
        public LoginCardPosition CardPosition { get; set; } = LoginCardPosition.Center;
    }
}