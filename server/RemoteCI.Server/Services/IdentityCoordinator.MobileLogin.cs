using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 手机扫码登录：WebUI 为当前登录账号签发一次性登录票据，写进二维码；
/// 安卓版扫码后凭票据换取正常的设备会话，不需要再输入密码。
/// </summary>
public sealed partial class IdentityCoordinator
{
    /// <summary>扫码登录票据有效期：二维码只在短时间内可用，过期后需在 WebUI 重新生成。</summary>
    public static readonly TimeSpan MobileLoginTicketLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 票据只保存在内存中（键为 SHA-256 摘要）：服务端重启即全部失效，
    /// 每个账号同一时间只保留最新一张，兑换一次即删除。
    /// </summary>
    private static readonly ConcurrentDictionary<string, MobileLoginTicket> MobileLoginTickets = new();

    private sealed record MobileLoginTicket(Guid UserId, string? SecurityStamp, DateTimeOffset ExpiresAt);

    /// <summary>为账号签发新的扫码登录票据，并作废该账号此前的票据。</summary>
    public async Task<IssuedMobileLoginTicket> CreateMobileLoginTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "账号不存在");
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, ticket) in MobileLoginTickets)
        {
            if (ticket.UserId == userId || ticket.ExpiresAt <= now) MobileLoginTickets.TryRemove(key, out _);
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expiresAt = now + MobileLoginTicketLifetime;
        MobileLoginTickets[Hash(token)] = new MobileLoginTicket(user.Id, user.SecurityStamp, expiresAt);
        return new IssuedMobileLoginTicket(token, user.UserName ?? string.Empty, expiresAt);
    }

    /// <summary>兑换扫码登录票据：票据一次性使用，账号停用、改密或待设密码时拒绝。</summary>
    public async Task<AuthResponse> RedeemMobileLoginTicketAsync(MobileLoginRequest request, CancellationToken ct = default)
    {
        const string invalidMessage = "登录二维码无效或已过期，请在 WebUI 重新生成";
        if (!MobileLoginTickets.TryRemove(Hash(request.Ticket.Trim()), out var ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, invalidMessage);
        var user = await users.FindByIdAsync(ticket.UserId.ToString());
        if (user is null || !user.Enabled || user.PasswordPending || user.SecurityStamp != ticket.SecurityStamp ||
            await users.IsLockedOutAsync(user))
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, invalidMessage);
        var deviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? "Android" : request.DeviceName.Trim();
        return await CreateOrRotateSessionAsync(user, deviceName, null, ct);
    }
}

/// <summary>新签发的扫码登录票据；明文票据只出现在二维码中。</summary>
public sealed record IssuedMobileLoginTicket(string Ticket, string Username, DateTimeOffset ExpiresAt);
