using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace RemoteCI.Server.Pages.Mobile;
[Authorize]
public sealed class DesktopModel : PageModel { public void OnGet() { } }
