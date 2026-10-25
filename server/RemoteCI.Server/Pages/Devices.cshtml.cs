using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>设备状态大屏：所有教室电脑的在线情况、版本、当前课程与今日在线时间条，每 15 秒刷新。</summary>
[Authorize]
public sealed class DevicesModel(
    UserManager<AppUser> users,
    DeviceHealthService health,
    ScreenshotDispatcher screenshots,
    IdentityCoordinator identities) : WebPageModel(users)
{
    [BindProperty(SupportsGet = true)] public string? Group { get; set; }
    [BindProperty(SupportsGet = true)] public bool OfflineOnly { get; set; }

    public DeviceHealthOverview Overview { get; private set; } = null!;
    public IReadOnlyList<string> GroupOptions { get; private set; } = [];
    public IReadOnlyList<DeviceHealthItem> Visible { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } denied) return denied;
        return Page();
    }

    /// <summary>页面定时刷新只替换设备网格，筛选条件与滚动位置保持不变。</summary>
    public async Task<IActionResult> OnGetGridAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } denied) return denied;
        Response.Headers.CacheControl = "no-store";
        return Partial("_DeviceGrid", this);
    }

    /// <summary>截取指定教室电脑的屏幕（系统管理员）；返回与控制页相同的 JSON。</summary>
    public async Task<IActionResult> OnPostScreenshotAsync(Guid classId, Guid credentialId, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.Forbidden, "仅系统管理员可以按设备截图"));
        if (await identities.GetProfileAsync(CurrentUser.Id, ct) is not { } profile)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.Unauthorized, "登录已失效"));
        var outcome = await screenshots.CaptureAsync(classId, credentialId, profile.WithPermissions(UserPermissions.All), ct);
        return new JsonResult(new
        {
            success = outcome.Record is not null,
            message = outcome.Record is null ? outcome.Result.Message : "截图已获取",
            screenshot = outcome.Record is { } record ? ControlModel.ScreenshotPageView(Url, record) : null,
        });
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        Overview = await health.GetOverviewAsync(null, ct);
        GroupOptions = Overview.Devices.SelectMany(x => x.GroupNames).Distinct().Order(StringComparer.CurrentCulture).ToList();
        Visible = Overview.Devices
            .Where(x => !OfflineOnly || !x.Online)
            .Where(x => string.IsNullOrEmpty(Group) || x.GroupNames.Contains(Group))
            .ToList();
        return null;
    }

    /// <summary>时间条片段在当天 0 点至现在之间的百分比位置。</summary>
    public (double Left, double Width) Position(DeviceTimelineSegment segment)
    {
        if (Overview.TimelineStart is not { } start || Overview.TimelineEnd is not { } end || end <= start) return (0, 0);
        var total = (end - start).TotalSeconds;
        var left = Math.Clamp((segment.Start - start).TotalSeconds / total * 100, 0, 100);
        var right = Math.Clamp((segment.End - start).TotalSeconds / total * 100, 0, 100);
        return (Math.Round(left, 2), Math.Round(Math.Max(right - left, 0.4), 2));
    }
}
