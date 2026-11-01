using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 课表页与我的日程共用的“显示设置”处理：周参数解析与学科颜色保存。
/// 学科颜色由 js/schedule-grid.js 以 Accept: application/json 提交并就地更新色块；不带脚本时回到原页面。
/// </summary>
public static class ScheduleDisplayHandler
{
    /// <summary>?week= 指定的那一周的周一；省略或无法解析时为 null（本周）。</summary>
    public static DateOnly? WeekStart(string? week) => SemesterWeeks.Parse(week) is { } date ? SemesterWeeks.Monday(date) : null;

    public static async Task<IActionResult> SaveColorAsync(
        PageModel page, SubjectColorService colors, Guid userId, string? name, string? color, bool reset, CancellationToken ct)
    {
        var json = page.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var week = page.Request.Query["week"].ToString();
        try
        {
            if (reset) await colors.ResetAsync(userId, name, ct);
            else await colors.SetAsync(userId, name, color, ct);
        }
        catch (IdentityOperationException ex)
        {
            if (json) return new JsonResult(new { message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
            page.TempData["Error"] = ex.Message;
            return page.RedirectToPage(new { week = string.IsNullOrEmpty(week) ? null : week });
        }
        if (!json) return page.RedirectToPage(new { week = string.IsNullOrEmpty(week) ? null : week });
        var key = SubjectColors.Key(name);
        var overrides = await colors.OverridesAsync(userId, ct);
        var resolved = SubjectColorService.Resolve(overrides, key);
        var light = SubjectColors.Derive(resolved, dark: false);
        var dark = SubjectColors.Derive(resolved, dark: true);
        return new JsonResult(new
        {
            name = key,
            color = resolved,
            custom = overrides.ContainsKey(key),
            light = new { background = light.Background, foreground = light.Foreground },
            dark = new { background = dark.Background, foreground = dark.Foreground },
        });
    }
}
