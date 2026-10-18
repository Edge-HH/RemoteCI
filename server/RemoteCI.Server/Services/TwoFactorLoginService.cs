using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>密码/登录票据通过后仅签发短时随机挑战，不创建已登录会话。</summary>
public sealed class TwoFactorLoginService(IMemoryCache cache)
{
    private const string CookieName = "RemoteCI.SecondFactor";
    public sealed record Challenge(Guid UserId, string? Stamp, bool RememberMe, string Target);
    private static string CacheKey(string token) => "totp-login:" + token;
    private static string CookiePath(HttpContext context) => context.Request.PathBase + "/LoginSecondFactor";

    public async Task BeginAsync(HttpContext context, AppUser user, bool rememberMe, string target)
    {
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cache.Set(CacheKey(token), new Challenge(user.Id, user.SecurityStamp, rememberMe, target), TimeSpan.FromMinutes(5));
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = CookiePath(context), MaxAge = TimeSpan.FromMinutes(5), IsEssential = true,
        });
    }

    public Challenge? Get(HttpContext context) => context.Request.Cookies[CookieName] is { Length: 64 } token
        ? cache.Get<Challenge>(CacheKey(token)) : null;

    public void Clear(HttpContext context)
    {
        if (context.Request.Cookies[CookieName] is { } token) cache.Remove(CacheKey(token));
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath(context) });
    }
}
