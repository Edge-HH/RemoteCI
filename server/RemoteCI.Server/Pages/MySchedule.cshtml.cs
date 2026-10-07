using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class MyScheduleModel(
    UserManager<AppUser> users,
    TeacherBindingService teachers) : WebPageModel(users)
{
    public MyScheduleResponse Schedule { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!AccountRole.HasPersonalSchedule(CurrentUser.RoleDefinitionId))
            return RedirectToPage("/Denied");

        Schedule = await teachers.BuildMyScheduleAsync(CurrentUser.DisplayName, ct);
        return Page();
    }

    public static string FormatDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MM月dd日 dddd", CultureInfo.GetCultureInfo("zh-CN"))
            : value;
}
