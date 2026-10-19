using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public static class ActionLinkActions
{
    public const string SwapApprove = "swap.approve";
    public const string SwapReject = "swap.reject";
    public const string SwapRevoke = "swap.revoke";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { SwapApprove, SwapReject, SwapRevoke };

    public static string Label(string action) => action switch
    {
        SwapApprove => "同意换课",
        SwapReject => "拒绝换课",
        SwapRevoke => "撤回强制换课",
        _ => "操作",
    };
}

public sealed class ActionLinkRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("targetId")] public Guid TargetId { get; set; }
}

public sealed record ActionLinkResponse(string Url, string Path, DateTimeOffset ExpiresAt);

/// <summary>一次性链接的当前状态，供确认页展示。</summary>
public sealed record ActionLinkState(ActionLinkToken? Token, SwapRequestView? Swap, string? Problem);

/// <summary>
/// 为聊天机器人卡片签发一次性确认链接。签发时校验调用者确实能执行该动作；打开链接只展示，
/// 确认提交时原子标记已使用，再经换课服务执行，审计与通知和其他入口一致。
/// </summary>
public sealed class ActionLinkService(AppDbContext db, ScheduleSwapService swaps, TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<ActionLinkResponse> CreateAsync(Guid userId, ActionLinkRequest request, string baseUrl, CancellationToken ct = default)
    {
        if (!ActionLinkActions.All.Contains(request.Action)) throw new ArgumentException("不支持的动作。");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.Enabled, ct)
            ?? throw new UnauthorizedAccessException("账号不可用。");
        var swap = await swaps.GetAsync(userId, request.TargetId, ct);
        var allowed = request.Action == ActionLinkActions.SwapRevoke ? swap.CanRevoke : swap.CanDecide;
        if (!allowed) throw new ArgumentException("该换课申请当前不能由你执行此操作。");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + Lifetime;
        db.ActionLinkTokens.Add(new ActionLinkToken
        {
            TokenHash = CalendarFeedService.Hash(token),
            UserId = userId,
            Action = request.Action,
            TargetId = request.TargetId,
            SecurityStamp = user.SecurityStamp,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        });
        // 顺带清理过期一周以上的链接。
        var stale = now.AddDays(-7).ToUnixTimeMilliseconds();
        await db.ActionLinkTokens.Where(x => x.ExpiresAtUnixMs < stale).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        var path = $"/Act/{token}";
        return new ActionLinkResponse(baseUrl.TrimEnd('/') + path, path, expiresAt);
    }

    public async Task<ActionLinkState> InspectAsync(string token, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct);
        if (row is null) return new ActionLinkState(null, null, "链接无效或已过期。");
        if (row.UsedAtUnixMs is not null) return new ActionLinkState(row, await TryGetSwapAsync(row, ct), "这个链接已经使用过。");
        if (Problem(row) is { } problem) return new ActionLinkState(row, null, problem);
        var swap = await TryGetSwapAsync(row, ct);
        if (swap is null) return new ActionLinkState(row, null, "换课申请不存在或你已无权查看。");
        var allowed = row.Action == ActionLinkActions.SwapRevoke ? swap.CanRevoke : swap.CanDecide;
        return new ActionLinkState(row, swap, allowed ? null : "该换课申请已处理或已失效，无需再操作。");
    }

    /// <summary>确认执行：先原子标记已使用（并发或重复提交只有一次成功），再调用换课服务。</summary>
    public async Task<SwapRequestView> ExecuteAsync(string token, string? note, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct) ?? throw new ArgumentException("链接无效或已过期。");
        if (Problem(row) is { } problem) throw new ArgumentException(problem);
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var claimed = await db.ActionLinkTokens
            .Where(x => x.TokenHash == row.TokenHash && x.UsedAtUnixMs == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.UsedAtUnixMs, nowMs), ct);
        if (claimed == 0) throw new ArgumentException("这个链接已经使用过。");
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        try
        {
            return row.Action switch
            {
                ActionLinkActions.SwapApprove => await swaps.ApproveAsync(row.UserId, row.TargetId, trimmed, ct),
                ActionLinkActions.SwapReject => await swaps.RejectAsync(row.UserId, row.TargetId, trimmed, ct),
                ActionLinkActions.SwapRevoke => await swaps.RevokeForcedAsync(row.UserId, row.TargetId, ct),
                _ => throw new ArgumentException("不支持的动作。"),
            };
        }
        catch (SwapOperationException)
        {
            // 执行失败（例如教室电脑离线、课位已变化）时恢复链接，稍后可以再试。
            await db.ActionLinkTokens.Where(x => x.TokenHash == row.TokenHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.UsedAtUnixMs, (long?)null), CancellationToken.None);
            throw;
        }
    }

    private async Task<ActionLinkToken?> FindAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = CalendarFeedService.Hash(token);
        return await db.ActionLinkTokens.AsNoTracking().Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    }

    private string? Problem(ActionLinkToken row)
    {
        if (row.ExpiresAtUnixMs <= timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) return "链接已过期，请在 RemoteCI 中处理。";
        if (!row.User.Enabled || !string.Equals(row.User.SecurityStamp, row.SecurityStamp, StringComparison.Ordinal))
            return "账号状态已变化，链接已失效，请在 RemoteCI 中处理。";
        return null;
    }

    private async Task<SwapRequestView?> TryGetSwapAsync(ActionLinkToken row, CancellationToken ct)
    {
        try { return await swaps.GetAsync(row.UserId, row.TargetId, ct); }
        catch (SwapOperationException) { return null; }
    }
}
