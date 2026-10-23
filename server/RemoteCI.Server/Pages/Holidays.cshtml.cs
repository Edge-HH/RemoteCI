using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>系统管理员设置学期开始日期，查看节假日数据状态，调整调休上学日补哪天的课。</summary>
[Authorize]
public sealed class HolidaysModel(UserManager<AppUser> users, HolidayCalendarService holidays, SemesterSettingsService semester) : WebPageModel(users)
{
    public static readonly string[] WeekdayNames = ["", "周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    [BindProperty] public SettingsInput Settings { get; set; } = new();
    public HolidayOverview Overview { get; private set; } = null!;
    [BindProperty] public SemesterSettings Semester { get; set; } = new();
    public int? CurrentWeek { get; private set; }

    public static string WeekdayOf(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? WeekdayNames[HolidayCalendarBuilder.IsoWeekday(day)]
            : string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        Overview = await holidays.GetOverviewAsync(ct);
        Settings = new SettingsInput { Enabled = Overview.Enabled, SourceUrlTemplate = Overview.SourceUrlTemplate };
        var view = await semester.GetViewAsync(ct);
        Semester = new SemesterSettings { StartDate = view.StartDate, SyncToClassrooms = view.SyncToClassrooms };
        CurrentWeek = view.CurrentWeek;
        return Page();
    }

    public async Task<IActionResult> OnPostSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await holidays.UpdateSettingsAsync(Settings.Enabled, Settings.SourceUrlTemplate, ct);
            TempData["Message"] = "调休设置已保存。";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSemesterAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            var saved = await semester.SetAsync(Semester, ct);
            TempData["Message"] = saved.StartDate is null ? "已清除学期开始日期。"
                : saved.SyncToClassrooms ? "学期开始日期已保存，并已同步到在线的教室端。" : "学期开始日期已保存。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostOverrideAsync(string date, string follow, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            TempData["Error"] = "日期无效。";
            return RedirectToPage();
        }
        try
        {
            if (follow == "auto") await holidays.RemoveOverrideAsync(day, ct);
            else await holidays.SetOverrideAsync(day, follow == "skip" ? null : int.Parse(follow, CultureInfo.InvariantCulture), CurrentUser.Id, ct);
            TempData["Message"] = $"{date} 的补课安排已更新。";
        }
        catch (Exception ex) when (ex is HolidayOperationException or ArgumentException or FormatException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var status = await holidays.RefreshAsync(ct);
        TempData[status.LastError is null ? "Message" : "Error"] = status.LastError ?? "节假日数据已刷新。";
        return RedirectToPage();
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    public sealed class SettingsInput
    {
        public bool Enabled { get; set; }
        public string? SourceUrlTemplate { get; set; }
    }
}
