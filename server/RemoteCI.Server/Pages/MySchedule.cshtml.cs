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
    PersonalScheduleService schedule) : WebPageModel(users)
{
    public MyScheduleResponse Schedule { get; private set; } = new();
    public List<PersonalCourseView> Courses { get; private set; } = [];
    public PersonalCourseView? Editing { get; private set; }
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string? Location { get; set; }
    [BindProperty] public string StartsAt { get; set; } = "";
    [BindProperty] public string EndsAt { get; set; } = "";
    [BindProperty] public int? ReminderMinutes { get; set; } = 10;
    [BindProperty] public bool NotifyAtStart { get; set; } = true;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? edit, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await LoadAsync(ct);
        Editing = Courses.FirstOrDefault(x => x.Id == edit);
        if (Editing is { } course)
        {
            Subject = course.Subject; Location = course.Location;
            StartsAt = course.StartsAt.ToString("O"); EndsAt = course.EndsAt.ToString("O");
            ReminderMinutes = course.ReminderMinutes; NotifyAtStart = course.NotifyAtStart;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ModelState.IsValid || !DateTimeOffset.TryParseExact(StartsAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateTimeOffset.TryParseExact(EndsAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            Error = "请选择有效的课程起止时间";
        else try
        {
            await schedule.SaveAsync(CurrentUser.Id, id, new PersonalCourseRequest
            {
                Subject = Subject, Location = Location ?? "", StartsAt = start, EndsAt = end,
                ReminderMinutes = ReminderMinutes, NotifyAtStart = NotifyAtStart,
            }, ct);
            return RedirectToPage();
        }
        catch (IdentityOperationException ex) { Error = ex.Message; }
        await LoadAsync(ct);
        Editing = Courses.FirstOrDefault(x => x.Id == id);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try { await schedule.DeleteAsync(CurrentUser.Id, id, ct); }
        catch (IdentityOperationException ex) { Error = ex.Message; await LoadAsync(ct); return Page(); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetCoursesAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return new JsonResult(await schedule.ListAsync(CurrentUser.Id, ct));
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Courses = await schedule.ListAsync(CurrentUser.Id, ct);
        Schedule = await schedule.BuildAsync(CurrentUser.Id,
            AccountRole.HasPersonalSchedule(CurrentUser.RoleDefinitionId) ? CurrentUser.DisplayName : null, ct: ct);
    }

    public static string FormatDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MM月dd日 dddd", CultureInfo.GetCultureInfo("zh-CN"))
            : value;
}
