using Microsoft.AspNetCore.WebUtilities;

namespace RemoteCI.Server.Services;

/// <summary>Only document navigation participates in UI selection; API and form requests retain their original contracts.</summary>
public static class MobileUi
{
    public const string Cookie = "RemoteCI.UiMode";
    public const string BrowserCookie = "RemoteCI.PushBrowser";
    public static string Browser(HttpContext context)
    {
        var value = context.Items[BrowserCookie] as string ?? context.Request.Cookies[BrowserCookie];
        if (value?.Length == 32 && Guid.TryParseExact(value, "N", out _)) return value;
        value = Guid.NewGuid().ToString("N");
        context.Items[BrowserCookie] = value;
        context.Response.Cookies.Append(BrowserCookie, value, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
        return value;
    }
    public static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = "/Mobile/Today", ["/Index"] = "/Mobile/Today", ["/Schedule"] = "/Mobile/Schedule",
        ["/MySchedule"] = "/Mobile/MySchedule", ["/SwapRequests"] = "/Mobile/SwapRequests",
        ["/Control"] = "/Mobile/Control", ["/Notifications"] = "/Mobile/Control", ["/Users"] = "/Mobile/Users",
        ["/Account"] = "/Mobile/Account", ["/Inbox"] = "/Mobile/Inbox", ["/SystemConfig"] = "/Mobile/SystemConfig",
    };
    private static readonly HashSet<string> DesktopOnly = new(StringComparer.OrdinalIgnoreCase)
    { "/Profiles", "/ClassProfiles", "/BatchControl", "/Classes", "/ExtensionSettings", "/ClassExtensions", "/Holidays", "/LoginSettings" };
    public static string SafeDesktopPage(string? path) => path is not null && DesktopOnly.Contains(path) ? path : "/Index";
    public static bool IsPhone(HttpRequest request) => request.Headers["Sec-CH-UA-Mobile"] == "?1" ||
        System.Text.RegularExpressions.Regex.IsMatch(request.Headers.UserAgent.ToString(), "iPhone|iPod|Android.*Mobile|Windows Phone", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public static bool IsMobile(HttpContext context) => context.Request.Path.StartsWithSegments("/Mobile") ||
        (context.Items[Cookie] as string ?? context.Request.Cookies[Cookie]) switch
        {
            "desktop" => false, "mobile" => true, _ => IsPhone(context.Request),
        };
    public static string DesktopPath(string path) => Pages.FirstOrDefault(x => x.Value.Equals(path, StringComparison.OrdinalIgnoreCase)).Key is { } match
        ? match == "/" ? "/Index" : match : "/Index";

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var document = HttpMethods.IsGet(request.Method) && !request.Query.ContainsKey("handler") &&
            (path == "/" || Pages.ContainsKey(path) || path.StartsWith("/Mobile", StringComparison.OrdinalIgnoreCase) || DesktopOnly.Contains(path) ||
             path is "/Login" or "/SetupPassword" or "/ClassSelect" or "/Visitor" or "/Setup" or "/Denied");
        if (!document) { await next(context); return; }
        Browser(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Append("Vary", "Cookie, User-Agent, Sec-CH-UA-Mobile");
        var selected = request.Query["view"].ToString();
        if (selected is "mobile" or "desktop" or "auto")
        {
            if (selected == "auto") context.Response.Cookies.Delete(Cookie);
            else context.Response.Cookies.Append(Cookie, selected, new CookieOptions
            { HttpOnly = true, Secure = request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
            context.Items[Cookie] = selected;
            var remaining = request.Query.Where(x => x.Key != "view").ToDictionary(x => x.Key, x => x.Value);
            var target = selected == "desktop" && path.StartsWith("/Mobile", StringComparison.OrdinalIgnoreCase)
                ? path.Equals("/Mobile/Desktop", StringComparison.OrdinalIgnoreCase) && request.Query["page"].ToString() is { } original && DesktopOnly.Contains(original)
                    ? original : DesktopPath(path)
                : selected == "mobile" && Pages.TryGetValue(path, out var phonePath) ? phonePath : path;
            remaining.Remove("page");
            context.Response.Redirect(request.PathBase + target + QueryString.Create(remaining));
            return;
        }
        if (IsMobile(context) && !request.Path.StartsWithSegments("/Mobile"))
        {
            if (Pages.TryGetValue(path, out var mobilePath))
            { context.Response.Redirect(request.PathBase + mobilePath + request.QueryString); return; }
            if (DesktopOnly.Contains(path))
            { context.Response.Redirect(request.PathBase + QueryHelpers.AddQueryString("/Mobile/Desktop", "page", path)); return; }
        }
        await next(context);
    }
}
