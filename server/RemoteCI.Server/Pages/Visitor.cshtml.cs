using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[AllowAnonymous]
public sealed class VisitorModel(VisitorAccessSettings visitorAccess, IStateStore state) : PageModel
{
    public ScheduleBundle? Bundle { get; private set; }

    /// <summary>开放访客功能的班级；访客页只能在它们之间切换。</summary>
    public IReadOnlyList<Classroom> VisitorClasses { get; private set; } = [];

    public Guid CurrentClassId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? @class, CancellationToken ct)
    {
        VisitorClasses = await visitorAccess.ListVisitorClassroomsAsync(ct);
        // 没有任何班级开放访客时与旧行为一致：直接回登录页。
        if (VisitorClasses.Count == 0)
            return RedirectToPage("/Login");
        CurrentClassId = @class is { } id && VisitorClasses.Any(x => x.Id == id)
            ? id
            : VisitorClasses[0].Id;
        Bundle = state.GetLatestSchedule(CurrentClassId);
        return Page();
    }
}
