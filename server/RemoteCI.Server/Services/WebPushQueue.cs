using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class WebPushSignal
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private long activeUntil;
    public bool Active => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Interlocked.Read(ref activeUntil);
    public void Wake()
    {
        Interlocked.Exchange(ref activeUntil, DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
        if (signal.CurrentCount == 0) try { signal.Release(); } catch (SemaphoreFullException) { }
    }
    public Task<bool> WaitAsync(CancellationToken ct) => signal.WaitAsync(Active ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), ct);
}

public sealed class WebPushQueue(AppDbContext db, ClassAccessService access, TimeProvider time, WebPushSignal signal)
{
    public static int Bit(ClassEventKind kind) => (int)kind is >= 1 and <= 7 ? 1 << ((int)kind - 1) : 0;
    public static bool IsTrustedEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
        new[] { "push.apple.com", "fcm.googleapis.com", "android.googleapis.com", "notify.windows.com", "push.services.mozilla.com" }
            .Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    public void Add(WebPushSubscription subscription, string sourceId, object payload, DateTimeOffset expires,
        Guid? notificationId = null, Guid? classId = null, int bit = 0)
    {
        db.WebPushJobs.Add(new WebPushJob { SubscriptionId = subscription.Id, SourceId = sourceId,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.Options), DueAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
            ExpiresAt = expires.ToUnixTimeMilliseconds(), NotificationId = notificationId, ClassId = classId, EventBit = bit });
        signal.Wake();
    }

    public async Task ClassEventAsync(Guid classId, ClassEvent value, CancellationToken ct)
    {
        var bit = Bit(value.Event);
        if (bit == 0 || string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 160) return;
        var subscriptions = await db.WebPushSubscriptions.Include(x => x.User)
            .Where(x => x.ClassId == classId && (x.EventMask & bit) != 0 && x.User.Enabled).ToListAsync(ct);
        var title = value.Event switch { ClassEventKind.OnClass => "上课提醒", ClassEventKind.OnBreaking => "下课提醒",
            ClassEventKind.OnAfterSchool => "放学提醒", ClassEventKind.ScheduleChanged => "课表已更新", _ => value.Subject ?? "RemoteCI" };
        foreach (var subscription in subscriptions)
        {
            if (!await CanAccessAsync(subscription, classId, ct)) continue;
            var sourceId = $"class:{classId:N}:{value.Id}";
            if (await db.WebPushJobs.AnyAsync(x => x.SubscriptionId == subscription.Id && x.SourceId == sourceId, ct)) continue;
            Add(subscription, sourceId, new { title = title[..Math.Min(title.Length, 120)], body = (value.Message ?? value.Subject ?? "")[..Math.Min((value.Message ?? value.Subject ?? "").Length, 1000)], kind = "class_event", id = sourceId,
                url = "/Schedule", classId }, value.OccurredAt.AddMinutes(5), classId: classId, bit: bit);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> CanAccessAsync(WebPushSubscription sub, Guid classId, CancellationToken ct)
    {
        var classes = await access.GetAccessibleClassesAsync(sub.UserId, sub.User.Role, sub.User.GrantedPermissions, ct);
        return classes.Any(x => x.Id == classId);
    }
}

public sealed class WebPushWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<WebPushWorker> logger, WebPushSignal signal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Web Push 队列处理失败"); }
            await signal.WaitAsync(stoppingToken);
        }
    }

    public async Task DispatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stamp = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ids = await db.WebPushJobs.Where(x => !x.Finished && x.DueAt <= stamp)
            .OrderBy(x => x.DueAt).Select(x => x.Id).Take(100).ToListAsync(ct);
        if (ids.Count > 0) logger.LogDebug("Web Push 本轮待投递 {Count}", ids.Count);
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (id, token) => await ProcessAsync(id, token));
        var retention = time.GetUtcNow().AddDays(-7).ToUnixTimeMilliseconds();
        await db.WebPushJobs.Where(x => x.ExpiresAt < retention).ExecuteDeleteAsync(ct);
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<WebPushSender>();
        var queue = scope.ServiceProvider.GetRequiredService<WebPushQueue>();
        var now = time.GetUtcNow();
        var stamp = now.ToUnixTimeMilliseconds();
        // Atomic lease, independent contexts per delivery. Other processes cannot claim the same queued item.
        if (await db.WebPushJobs.Where(x => x.Id == id && !x.Finished && x.DueAt <= stamp)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.DueAt, now.AddMinutes(1).ToUnixTimeMilliseconds())
                .SetProperty(y => y.Attempts, y => y.Attempts + 1), ct) == 0) return;
        var job = await db.WebPushJobs.Include(x => x.Subscription).ThenInclude(x => x.User).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return;
        var subscription = job.Subscription;
        if (job.ExpiresAt <= stamp || !subscription.User.Enabled || !WebPushQueue.IsTrustedEndpoint(subscription.Endpoint) ||
            job.ClassId is { } c && (subscription.ClassId != c || (subscription.EventMask & job.EventBit) == 0 || !await queue.CanAccessAsync(subscription, c, ct)) ||
            job.NotificationId is { } n && !await db.UserNotifications.AnyAsync(x => x.Id == n && x.UserId == subscription.UserId && x.ReadAt == null, ct))
        { job.Finished = true; await db.SaveChangesAsync(ct); return; }
        try
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(job.Payload)!;
            payload["unread"] = await db.UserNotifications.CountAsync(x => x.UserId == subscription.UserId && x.ReadAt == null, ct);
            var result = await sender.DeliverAsync(await sender.GetKeysAsync(db, ct),
                new WebPushTarget(subscription.Id, subscription.Endpoint, subscription.P256dh, subscription.Auth),
                System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()), Math.Max(1, (int)((job.ExpiresAt - stamp) / 1000)), ct);
            if (result.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            { db.WebPushSubscriptions.Remove(subscription); job.Finished = true; logger.LogInformation("已清理失效 Web Push 订阅 {SubscriptionId}", subscription.Id); }
            else if ((int)result.Status < 300) { job.Finished = true; logger.LogDebug("Web Push 投递成功 {JobId}", job.Id); }
            else if (result.Status == HttpStatusCode.TooManyRequests || (int)result.Status >= 500) Retry(job, now, result.RetryAfter);
            else { job.Finished = true; logger.LogWarning("Web Push 最终失败 {JobId}: HTTP {Status}", job.Id, (int)result.Status); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { if (ct.IsCancellationRequested) throw; Retry(job, now, null); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException or JsonException)
        { job.Finished = true; logger.LogWarning("Web Push 无效载荷或密钥 {JobId}", job.Id); }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* Logout or subscription rotation removed the claimed job. */ }
    }

    private void Retry(WebPushJob job, DateTimeOffset now, TimeSpan? retryAfter)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(job.Attempts switch { 1 => 10, 2 => 30, 3 => 120, _ => 300 });
        job.DueAt = now.Add(delay < TimeSpan.Zero ? TimeSpan.FromSeconds(10) : delay).ToUnixTimeMilliseconds();
        if (job.DueAt >= job.ExpiresAt) job.Finished = true;
        logger.LogWarning("Web Push {JobId}: 第 {Attempts} 次失败，{Outcome}", job.Id, job.Attempts, job.Finished ? "已过期" : "等待重试");
    }
}
