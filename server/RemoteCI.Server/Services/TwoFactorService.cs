using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>RFC 6238，SHA-1 / 6 位 / 30 秒；只接受相邻一个时间窗口，成功后原子消费。</summary>
public sealed class TwoFactorService(AppDbContext db, UserManager<AppUser> users, IDataProtectionProvider protection)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private IDataProtector KeyProtector(Guid id) => protection.CreateProtector("RemoteCI.Totp.Key.v1", id.ToString());
    private IDataProtector SetupProtector => protection.CreateProtector("RemoteCI.Totp.Setup.v1");
    public string ProtectKey(Guid id, string secret) => KeyProtector(id).Protect(secret);
    public string UnprotectKey(AppUser user) => KeyProtector(user.Id).Unprotect(user.ProtectedTotpKey!);

    public sealed record Setup(Guid UserId, string? Stamp, string Secret, DateTimeOffset ExpiresAt);

    public string BeginSetup(AppUser user)
    {
        if (user.TwoFactorEnabled) throw Invalid("二步验证已经开启");
        // 160 位随机密钥恰好编码为 32 个 Base32 字符。
        var bytes = RandomNumberGenerator.GetBytes(20);
        var key = new StringBuilder(32);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { bits -= 5; key.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        return SetupProtector.Protect(JsonSerializer.Serialize(new Setup(user.Id, user.SecurityStamp, key.ToString(), DateTimeOffset.UtcNow.AddMinutes(10))));
    }

    public Setup ReadSetup(AppUser user, string token)
    {
        try
        {
            var setup = JsonSerializer.Deserialize<Setup>(SetupProtector.Unprotect(token));
            if (setup is not null && setup.UserId == user.Id && setup.Stamp == user.SecurityStamp &&
                setup.ExpiresAt > DateTimeOffset.UtcNow && !user.TwoFactorEnabled) return setup;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { }
        throw Invalid("绑定已过期，请重新开始绑定");
    }

    public async Task CheckPasswordAsync(AppUser user, string password)
    {
        if (!user.Enabled || user.PasswordPending || await users.IsLockedOutAsync(user))
            throw Invalid("账号不可用或已临时锁定，请稍后再试");
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            throw Invalid("当前密码错误");
        }
        // 密码正确不能清零验证码的失败计数，成功完成所有验证后才清零。
    }

    public async Task<string[]> EnableAsync(AppUser user, string setupToken, string code, CancellationToken ct)
    {
        var setup = ReadSetup(user, setupToken);
        if (await users.IsLockedOutAsync(user)) throw Invalid("失败次数过多，请稍后再试");
        var step = MatchTimeStep(setup.Secret, code, DateTimeOffset.UtcNow);
        if (step is null)
        {
            await users.AccessFailedAsync(user);
            throw Invalid("验证码错误或已过期，请检查认证器时间后重试");
        }
        var codes = Enumerable.Range(0, 10).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(8))).ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        user.ProtectedTotpKey = ProtectKey(user.Id, setup.Secret);
        user.LastTotpTimeStep = step;
        user.TwoFactorRecoveryHashes = string.Join(',', codes.Select(RecoveryHash));
        user.TwoFactorEnabled = true;
        user.AccessFailedCount = 0;
        await SaveSecurityChangeAsync(user, ct);
        await tx.CommitAsync(ct);
        return codes.Select(x => string.Join('-', Enumerable.Range(0, 4).Select(i => x.Substring(i * 4, 4)))).ToArray();
    }

    public async Task DisableAsync(AppUser user, string password, string code, bool recovery, CancellationToken ct)
    {
        await CheckPasswordAsync(user, password);
        if (!user.TwoFactorEnabled || !await VerifyAsync(user, code, recovery, ct))
            throw Invalid("验证码或恢复码无效");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        user.TwoFactorEnabled = false;
        user.ProtectedTotpKey = null;
        user.LastTotpTimeStep = null;
        user.TwoFactorRecoveryHashes = null;
        user.AccessFailedCount = 0;
        await SaveSecurityChangeAsync(user, ct);
        await tx.CommitAsync(ct);
    }

    private async Task SaveSecurityChangeAsync(AppUser user, CancellationToken ct)
    {
        await db.SystemMetadata.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(m => m.AccountVersion, m => m.AccountVersion + 1), ct);
        user.Version = await db.SystemMetadata.Where(x => x.Id == 1).Select(x => x.AccountVersion).SingleAsync(ct);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw Invalid("账号已更新，请刷新后重试");
        var now = DateTimeOffset.UtcNow;
        await db.DeviceSessions.Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
        await db.WebPushSubscriptions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> VerifyAsync(AppUser user, string? code, bool recovery, CancellationToken ct)
    {
        if (!user.Enabled || user.PasswordPending || !user.TwoFactorEnabled || await users.IsLockedOutAsync(user)) return false;
        var stamp = Guid.NewGuid().ToString();
        var query = db.Users.Where(x => x.Id == user.Id && x.Enabled && !x.PasswordPending && x.TwoFactorEnabled &&
            x.SecurityStamp == user.SecurityStamp && x.ConcurrencyStamp == user.ConcurrencyStamp);
        int changed = 0;
        if (recovery)
        {
            var old = user.TwoFactorRecoveryHashes;
            var hashes = (old ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var hash = RecoveryHash(code ?? "");
            if (hashes.Remove(hash))
            {
                var remaining = string.Join(',', hashes);
                changed = await query.Where(x => x.TwoFactorRecoveryHashes == old).ExecuteUpdateAsync(x => x
                    .SetProperty(u => u.TwoFactorRecoveryHashes, remaining).SetProperty(u => u.ConcurrencyStamp, stamp), ct);
                if (changed == 1) user.TwoFactorRecoveryHashes = remaining;
            }
        }
        else if (user.ProtectedTotpKey is not null)
        {
            long? step = null;
            try { step = MatchTimeStep(UnprotectKey(user), code ?? "", DateTimeOffset.UtcNow); }
            catch (CryptographicException) { /* 密钥环不可用时仍可使用恢复码。 */ }
            if (step is { } matched && (user.LastTotpTimeStep is null || matched > user.LastTotpTimeStep))
            {
                changed = await query.Where(x => x.ProtectedTotpKey == user.ProtectedTotpKey &&
                    (x.LastTotpTimeStep == null || x.LastTotpTimeStep < matched)).ExecuteUpdateAsync(x => x
                    .SetProperty(u => u.LastTotpTimeStep, matched).SetProperty(u => u.ConcurrencyStamp, stamp), ct);
                if (changed == 1) user.LastTotpTimeStep = matched;
            }
        }
        if (changed != 1)
        {
            await users.AccessFailedAsync(user);
            return false;
        }
        // ExecuteUpdate 绕过跟踪器；同步并发标记，避免后续 Identity 更新覆盖已消费的验证码。
        user.ConcurrencyStamp = stamp;
        db.Entry(user).Property(x => x.ConcurrencyStamp).OriginalValue = stamp;
        return true;
    }

    private static string RecoveryHash(string code) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant())));

    internal static long? MatchTimeStep(string secret, string code, DateTimeOffset now)
    {
        code = code.Replace(" ", "").Replace("-", "");
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) return null;
        var step = now.ToUnixTimeSeconds() / 30;
        // 从新到旧匹配，避免极少见的相邻窗口验证码碰撞使重放检查失效。
        for (var candidate = step + 1; candidate >= step - 1; candidate--)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(CodeAt(secret, candidate)))) return candidate;
        return null;
    }

    internal static string CodeAt(string secret, long step, int digits = 6)
    {
        var key = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in secret)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Invalid Base32 key");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8) { bits -= 8; key.Add((byte)(buffer >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key.ToArray(), counter);
        var offset = hash[^1] & 15;
        var value32 = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value32 % (digits == 8 ? 100000000 : 1000000)).ToString(digits == 8 ? "D8" : "D6", CultureInfo.InvariantCulture);
    }

    private static IdentityOperationException Invalid(string message) => new(ApiErrorCodes.Unauthorized, message);
}
