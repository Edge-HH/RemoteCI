using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class VisitorAccessModel(UserManager<AppUser> users, VisitorAccessSettings settings, ClassroomService classrooms) : WebPageModel(users)
{
    [BindProperty] public bool AutoEnter { get; set; }
    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        AutoEnter = await settings.GetAutoEnterAsync(ct);
        if (CurrentUser.Role == UserRole.Admin) Classes = await classrooms.ListAsync(ct);
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        await settings.SetAutoEnterAsync(AutoEnter, ct); TempData["Message"] = "访客设置已保存"; return RedirectToPage();
    }
    public async Task<IActionResult> OnPostClassAsync(Guid id, bool enabled, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return Forbid();
        try { await classrooms.SetVisitorAccessAsync(id, enabled, ct); TempData["Message"] = "班级访客设置已保存"; }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }
}
