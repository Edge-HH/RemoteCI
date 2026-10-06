using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 客户端一键打开 WebUI：已登录的安卓版为当前账号签发一次性网页登录票据，
/// 用系统浏览器打开 /WebLogin?t=… 后服务端兑换票据并写入 WebUI 登录 Cookie。
/// </summary>
public sealed partial class IdentityCoordinator
{
    /// <summary>网页登录票据有效期：只用于“点击后立即跳转浏览器”，因此很短。</summary>
    public static readonly TimeSpan WebLoginTicketLifetime = TimeSpan.FromMinutes(1);

    /// <summary>为账号签发新的网页登录票据，并作废该账号此前的票据；顺带清理所有已过期票据。</summary>
    public async Task<IssuedWebLoginTicket> CreateWebLoginTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "账号不存在");
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await db.WebLoginTickets
            .Where(x => x.UserId == userId || x.ExpiresAtUnixMs <= nowMs)
            .ExecuteDeleteAsync(ct);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expiresAt = now + WebLoginTicketLifetime;
        db.WebLoginTickets.Add(new WebLoginTicket
        {
            TokenHash = Hash(token),
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync(ct);
        return new IssuedWebLoginTicket(token, expiresAt);
    }

    /// <summary>
    /// 兑换网页登录票据：一次性使用；账号停用、改密（安全戳变化）、锁定或待设密码时返回 null。
    /// 先读出票据再按主键删除，只有删除影响 1 行的请求才算兑换成功，并发或跨实例重复兑换都会失败。
    /// </summary>
    public async Task<AppUser?> RedeemWebLoginTicketAsync(string? ticket, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        var hash = Hash(ticket.Trim());
        var issued = await db.WebLoginTickets.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (issued is null) return null;
        if (await db.WebLoginTickets.Where(x => x.TokenHash == hash).ExecuteDeleteAsync(ct) != 1) return null;
        if (issued.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return null;
        var user = await users.FindByIdAsync(issued.UserId.ToString());
        if (user is null || !user.Enabled || user.PasswordPending || user.SecurityStamp != issued.SecurityStamp ||
            await users.IsLockedOutAsync(user))
            return null;
        return user;
    }
}

/// <summary>新签发的网页登录票据；明文票据只出现在一次性跳转链接中。</summary>
public sealed record IssuedWebLoginTicket(string Ticket, DateTimeOffset ExpiresAt);
