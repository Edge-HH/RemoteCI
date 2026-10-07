using System.Collections.Concurrent;
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

    /// <summary>票据只保存在内存中（键为 SHA-256 摘要），每个账号同一时间只保留最新一张，兑换一次即删除。</summary>
    private static readonly ConcurrentDictionary<string, WebLoginTicket> WebLoginTickets = new();

    private sealed record WebLoginTicket(Guid UserId, string? SecurityStamp, DateTimeOffset ExpiresAt);

    /// <summary>为账号签发新的网页登录票据，并作废该账号此前的票据。</summary>
    public async Task<IssuedWebLoginTicket> CreateWebLoginTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "账号不存在");
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, ticket) in WebLoginTickets)
        {
            if (ticket.UserId == userId || ticket.ExpiresAt <= now) WebLoginTickets.TryRemove(key, out _);
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expiresAt = now + WebLoginTicketLifetime;
        WebLoginTickets[Hash(token)] = new WebLoginTicket(user.Id, user.SecurityStamp, expiresAt);
        return new IssuedWebLoginTicket(token, expiresAt);
    }

    /// <summary>兑换网页登录票据：一次性使用；账号停用、改密、锁定或待设密码时返回 null。</summary>
    public async Task<AppUser?> RedeemWebLoginTicketAsync(string? ticket, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        if (!WebLoginTickets.TryRemove(Hash(ticket.Trim()), out var issued) || issued.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        var user = await users.FindByIdAsync(issued.UserId.ToString());
        if (user is null || !user.Enabled || user.PasswordPending || user.SecurityStamp != issued.SecurityStamp ||
            await users.IsLockedOutAsync(user))
            return null;
        return user;
    }
}

/// <summary>新签发的网页登录票据；明文票据只出现在一次性跳转链接中。</summary>
public sealed record IssuedWebLoginTicket(string Ticket, DateTimeOffset ExpiresAt);
