using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class CalendarModel(UserManager<AppUser> users, AppDbContext db, CalendarFeedService feed) : WebPageModel(users)
{
    public List<CalendarSubscription> Subscriptions { get; private set; } = [];
    public string? CreatedUrl { get; private set; }
    [BindProperty] public Guid? CalendarClassId { get; set; }
    [BindProperty] public int? Reminder { get; set; } = 10;
    [BindProperty] public int Offset { get; set; } = 480;
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Subscriptions = await db.CalendarSubscriptions.Where(x => x.UserId == CurrentUser.Id && !x.Revoked).ToListAsync(ct);
        return Page();
    }
    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ModelState.IsValid) { TempData["Error"] = "请选择有效的日历设置"; return RedirectToPage(); }
        try
        {
            var result = await feed.CreateAsync(CurrentUser, CalendarClassId, Reminder, Offset, ct);
            CreatedUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/calendar/{result.Token}.ics";
            Subscriptions = await db.CalendarSubscriptions.Where(x => x.UserId == CurrentUser.Id && !x.Revoked).ToListAsync(ct);
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            return Page();
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; return RedirectToPage(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await db.CalendarSubscriptions.Where(x => x.Id == id && x.UserId == CurrentUser.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Revoked, true), ct);
        TempData["Message"] = "已撤销链接。已下载的事件请在系统日历中取消订阅以移除。";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostReminderAsync(Guid id, int? reminder, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ModelState.IsValid || reminder is not (null or 0 or 5 or 10 or 15 or 30)) return BadRequest();
        await db.CalendarSubscriptions.Where(x => x.Id == id && x.UserId == CurrentUser.Id && !x.Revoked)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ReminderMinutes, reminder), ct);
        TempData["Message"] = "已保存，系统日历下次刷新时生效。自定义课程仍使用自身提醒设置。";
        return RedirectToPage();
    }
}
