using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 个人通知中心：换课申请等个人通知的列表、全部已读，以及浏览器通知（Web Push）的开启与关闭。
/// 顶栏铃铛的未读数由 notifications.js 轮询 Unread 处理器刷新（REST 只认 Bearer，网页用 Cookie 走页面处理器）。
/// </summary>
[Authorize]
public sealed class InboxModel(
    UserManager<AppUser> users,
    UserNotificationService notifications,
    WebPushSender webPush,
    AppDbContext db) : WebPageModel(users)
{
    public IReadOnlyList<UserNotificationView> Items { get; private set; } = [];
    public string VapidPublicKey { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Items = await notifications.ListAsync(CurrentUser.Id, null, false, 100, ct);
        VapidPublicKey = (await webPush.GetKeysAsync(db, ct)).PublicKey;
        return Page();
    }

    public async Task<IActionResult> OnPostReadAllAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await notifications.MarkReadAsync(CurrentUser.Id, null, true, ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReadAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await notifications.MarkReadAsync(CurrentUser.Id, [id], false, ct);
        return new JsonResult(new { ok = true });
    }

    /// <summary>铃铛轮询：未读数、待我处理的换课数与 after 之后的新通知（用于页内提示）。</summary>
    public async Task<IActionResult> OnGetUnreadAsync(DateTimeOffset? after, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return Unauthorized();
        var latest = after is null ? [] : await notifications.ListAsync(CurrentUser.Id, after, true, 10, ct);
        var swaps = HttpContext.RequestServices.GetRequiredService<ScheduleSwapService>();
        return new JsonResult(new
        {
            unread = await notifications.CountUnreadAsync(CurrentUser.Id, ct),
            pendingSwaps = await swaps.CountPendingForAsync(CurrentUser.Id, ct),
            serverTime = DateTimeOffset.UtcNow,
            items = latest,
        });
    }

    public async Task<IActionResult> OnPostSubscribeAsync([FromBody] WebPushSubscriptionRequest request, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return Unauthorized();
        try
        {
            await notifications.SubscribeWebPushAsync(CurrentUser.Id, request, ct);
            return new JsonResult(new { ok = true });
        }
        catch (IdentityOperationException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostUnsubscribeAsync([FromBody] WebPushSubscriptionRequest request, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return Unauthorized();
        await notifications.UnsubscribeWebPushAsync(CurrentUser.Id, request.Endpoint, ct);
        return new JsonResult(new { ok = true });
    }

    /// <summary>点击通知跳转到换课页的对应页签：发给我的申请在“待我处理”，处理结果在“我的申请”。</summary>
    public static string SwapTab(string kind) => kind switch
    {
        UserNotificationKinds.SwapRequested or UserNotificationKinds.SwapForced or UserNotificationKinds.SwapCancelled => "#incoming",
        UserNotificationKinds.SwapApproved or UserNotificationKinds.SwapRejected or UserNotificationKinds.SwapRevoked => "#outgoing",
        _ => "",
    };

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.UtcNow - value;
        if (span < TimeSpan.FromMinutes(1)) return "刚刚";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} 分钟前";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} 小时前";
        return value.ToLocalTime().ToString("M月d日 HH:mm");
    }
}
