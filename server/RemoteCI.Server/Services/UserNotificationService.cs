using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 个人通知：先落库（WebUI 铃铛、AstrBot 与手机离线补齐都读这份记录），
/// 再实时推送给该用户在线的手机/手表连接（user_notify）与已订阅的浏览器（Web Push）。
/// </summary>
public sealed class UserNotificationService(
    AppDbContext db,
    PeerRegistry peers,
    WebPushQueue queue,
    ILogger<UserNotificationService> logger)
{
    /// <summary>每个用户保留的通知条数上限，超出部分按时间最早删除。</summary>
    private const int MaxPerUser = 200;

    public async Task PublishAsync(
        IEnumerable<Guid> userIds, string kind, string title, string body, Guid? swapRequestId,
        CancellationToken ct = default, DateTimeOffset? expiresAt = null)
    {
        var recipients = userIds.Distinct().ToList();
        if (recipients.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var rows = recipients.Select(userId => new UserNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Title = Truncate(title, 120),
            Body = Truncate(body, 1000),
            SwapRequestId = swapRequestId,
            CreatedAt = now,
        }).ToList();
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        db.UserNotifications.AddRange(rows);
        var subscriptions = await db.WebPushSubscriptions.AsNoTracking().Where(x => recipients.Contains(x.UserId)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var view = ToView(row);
            foreach (var subscription in subscriptions.Where(x => x.UserId == row.UserId))
                queue.Add(subscription, "personal:" + row.Id, new { title = view.Title, body = view.Body,
                    kind = view.Kind, id = view.Id, url = view.Kind.StartsWith("personal_course_", StringComparison.Ordinal) ? "/MySchedule" : "/SwapRequests" },
                    expiresAt ?? (view.Kind.StartsWith("personal_course_", StringComparison.Ordinal) ? now.AddMinutes(5) : now.AddHours(24)), notificationId: row.Id);
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        foreach (var row in rows)
        {
            try { await peers.SendToUserAsync(row.UserId, Envelope.UserNotify(ToView(row)), ct); }
            catch (Exception ex) { logger.LogWarning(ex, "向用户在线连接推送个人通知失败"); }
        }
        await TrimAsync(recipients, ct);
    }

    public async Task<IReadOnlyList<UserNotificationView>> ListAsync(
        Guid userId, DateTimeOffset? after, bool unreadOnly, int limit, CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 比较/排序的 SQL 翻译；每人最多 200 条，取回后内存过滤。
        var rows = await db.UserNotifications.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        return rows
            .Where(x => after is null || x.CreatedAt > after)
            .Where(x => !unreadOnly || x.ReadAt is null)
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaxPerUser))
            .Select(ToView)
            .ToList();
    }

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken ct = default) =>
        db.UserNotifications.CountAsync(x => x.UserId == userId && x.ReadAt == null, ct);

    public async Task MarkReadAsync(Guid userId, IReadOnlyCollection<Guid>? ids, bool all, CancellationToken ct = default)
    {
        var query = db.UserNotifications.Where(x => x.UserId == userId && x.ReadAt == null);
        if (!all)
        {
            if (ids is not { Count: > 0 }) return;
            query = query.Where(x => ids.Contains(x.Id));
        }
        var now = DateTimeOffset.UtcNow;
        await query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAt, now), ct);
    }

    /// <summary>与某个换课申请关联的通知统一标记已读（例如对方已处理，提醒已无意义）。</summary>
    public async Task MarkSwapReadAsync(Guid userId, Guid swapRequestId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.UserNotifications
            .Where(x => x.UserId == userId && x.SwapRequestId == swapRequestId && x.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAt, now), ct);
    }

    public async Task SubscribeWebPushAsync(Guid userId, WebPushSubscriptionRequest request, CancellationToken ct = default)
    {
        if (!WebPushQueue.IsTrustedEndpoint(request.Endpoint) ||
            request.Endpoint.Length > 1024 || string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "浏览器推送订阅无效");
        try
        {
            if (WebPushSender.Base64UrlDecode(request.P256dh).Length != 65 || WebPushSender.Base64UrlDecode(request.Auth).Length < 16)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "浏览器推送订阅密钥无效");
        }
        var existing = await db.WebPushSubscriptions.SingleOrDefaultAsync(x => x.Endpoint == request.Endpoint, ct);
        if (existing is null)
        {
            db.WebPushSubscriptions.Add(new WebPushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Endpoint = request.Endpoint,
                P256dh = request.P256dh.Trim(),
                Auth = request.Auth.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            // 同一浏览器换账号登录：订阅归属最新登录的账号，避免把他人的通知推到这台浏览器。
            if (existing.UserId != userId)
                await db.WebPushJobs.Where(x => x.SubscriptionId == existing.Id).ExecuteDeleteAsync(ct);
            existing.ClassId = existing.UserId == userId ? existing.ClassId : null;
            existing.EventMask = existing.UserId == userId ? existing.EventMask : 0;
            existing.UserId = userId;
            existing.P256dh = request.P256dh.Trim();
            existing.Auth = request.Auth.Trim();
        }
        await db.SaveChangesAsync(ct);
    }

    public Task UnsubscribeWebPushAsync(Guid userId, string endpoint, CancellationToken ct = default) =>
        db.WebPushSubscriptions.Where(x => x.UserId == userId && x.Endpoint == endpoint).ExecuteDeleteAsync(ct);

    public static UserNotificationView ToView(UserNotification row) => new()
    {
        Id = row.Id,
        Kind = row.Kind,
        Title = row.Title,
        Body = row.Body,
        SwapRequestId = row.SwapRequestId,
        CreatedAt = row.CreatedAt,
        ReadAt = row.ReadAt,
    };

    private async Task TrimAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        foreach (var userId in userIds)
        {
            var count = await db.UserNotifications.CountAsync(x => x.UserId == userId, ct);
            if (count <= MaxPerUser) continue;
            var stale = (await db.UserNotifications.AsNoTracking().Where(x => x.UserId == userId)
                    .Select(x => new { x.Id, x.CreatedAt }).ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAt).Skip(MaxPerUser).Select(x => x.Id).ToList();
            await db.UserNotifications.Where(x => stale.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
