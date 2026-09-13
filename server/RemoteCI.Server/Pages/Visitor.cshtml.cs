using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[AllowAnonymous]
public sealed class VisitorModel(VisitorAccessSettings visitorAccess, IStateStore state) : PageModel
{
    public ScheduleBundle? Bundle { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!(await visitorAccess.GetAsync(ct)).Enabled)
            return RedirectToPage("/Login");

        Bundle = state.GetLatestSchedule();
        return Page();
    }
}
