using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class LogoutModel(SignInManager<Data.AppUser> signIn, Data.AppDbContext db, UserManager<Data.AppUser> users) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        var browser = Request.Cookies[MobileUi.BrowserCookie];
        if (Guid.TryParse(users.GetUserId(User), out var id) && browser is not null)
            await db.WebPushSubscriptions.Where(x => x.UserId == id && x.BrowserId == browser).ExecuteDeleteAsync();
        await signIn.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
