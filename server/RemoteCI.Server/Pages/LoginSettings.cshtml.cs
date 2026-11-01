using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 系统设置的“登录与访问”tab：登录页外观、手机扫码登录地址（系统管理员），以及访客自动进入（人员管理权限）。
/// </summary>
[Authorize]
public sealed class LoginSettingsModel(
    UserManager<AppUser> users,
    LoginPageSettings settings,
    MobileLoginSettings mobileLogin,
    VisitorAccessSettings visitorAccess) : WebPageModel(users)
{
    [BindProperty] public AppearanceInput Appearance { get; set; } = new();
    [BindProperty] public IFormFile? BackgroundFile { get; set; }
    [BindProperty] public string? MobileServerUrl { get; set; }
    [BindProperty] public bool AutoEnterVisitorPage { get; set; }

    /// <summary>登录页外观与扫码登录地址：拥有“登录页与访客”系统权限即可修改。</summary>
    public bool CanEditAppearance => HasManagement(ManagementPermissions.ManageLoginPage);
    /// <summary>逐班访客开关在班级管理中，有“班级管理”权限时给出链接。</summary>
    public bool CanManageClasses => HasManagement(ManagementPermissions.ManageClasses);

    public bool HasBackground { get; private set; }
    public DateTimeOffset? BackgroundUpdatedAt { get; private set; }
    public int MaxBackgroundKb => LoginPageSettings.MaxBackgroundBytes / 1024;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManageLoginPage) is { } denied) return denied;
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
        return RedirectToPage(null, null, "login-appearance");
    }

    public async Task<IActionResult> OnPostRemoveBackgroundAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await settings.SetBackgroundAsync(null, null, ct);
        TempData["Message"] = "已移除登录页背景图。";
        return RedirectToPage(null, null, "login-appearance");
    }

    public async Task<IActionResult> OnPostSaveMobileLoginSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await mobileLogin.SetServerUrlAsync(MobileServerUrl, ct);
            TempData["Message"] = string.IsNullOrWhiteSpace(MobileServerUrl)
                ? "扫码登录二维码将使用当前访问地址"
                : "扫码登录服务器地址已保存";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(null, null, "mobile-login");
    }

    public async Task<IActionResult> OnPostVisitorAccessAsync(CancellationToken ct)
    {
        if (await RequireManagementAsync(ManagementPermissions.ManageLoginPage) is { } denied) return denied;
        // 班级级“开放访客”开关在班级管理页逐班配置；这里只保留登录页自动进入的全局行为。
        var autoEnter = await visitorAccess.SetAutoEnterAsync(AutoEnterVisitorPage, ct);
        TempData["Message"] = autoEnter
            ? "已开启自动进入访客页。"
            : "已关闭自动进入访客页。";
        return RedirectToPage(null, null, "visitor");
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        AutoEnterVisitorPage = await visitorAccess.GetAutoEnterAsync(ct);
        MobileServerUrl = await mobileLogin.GetServerUrlAsync(ct);
        var appearance = await settings.GetAppearanceAsync(ct);
        Appearance = new AppearanceInput { Theme = appearance.Theme, Opacity = appearance.Opacity, CardPosition = appearance.CardPosition };
        HasBackground = appearance.HasBackground;
        BackgroundUpdatedAt = appearance.BackgroundUpdatedAt;
    }

    /// <summary>登录页外观与扫码登录地址属于系统级配置，需要“登录页与访客”系统权限。</summary>
    private Task<IActionResult?> RequireAdminAsync() => RequireManagementAsync(ManagementPermissions.ManageLoginPage);

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