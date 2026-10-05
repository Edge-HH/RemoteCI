using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>老师换课：发起申请（可强制）、处理别人发给我的申请、查看我发起的申请。</summary>
[Authorize]
public sealed class SwapRequestsModel(UserManager<AppUser> users, ScheduleSwapService swaps) : WebPageModel(users)
{
    public SwapCatalog Catalog { get; private set; } = new();
    public IReadOnlyList<SwapRequestView> Incoming { get; private set; } = [];
    public IReadOnlyList<SwapRequestView> Outgoing { get; private set; } = [];

    /// <summary>选择器数据以 JSON 内嵌到页面，由 swap-requests.js 联动班级→日期→节次。</summary>
    public string CatalogJson => JsonSerializer.Serialize(Catalog, JsonDefaults.Options);

    public int PendingIncoming => Incoming.Count(x => x.CanDecide || x.CanRevoke);

    [BindProperty] public SwapInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.RequestScheduleSwap) is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.RequestScheduleSwap) is { } denied) return denied;
        var request = new CreateSwapRequest
        {
            Mode = Input.Mode,
            Reason = Input.Reason ?? string.Empty,
            Force = Input.Force,
            SubjectName = Input.Mode == SwapMode.Replace ? Input.SubjectName : null,
            Target = new SwapSlot { ClassId = Input.TargetClassId, Date = Input.TargetDate ?? "", Index = Input.TargetIndex },
            Source = Input.Mode == SwapMode.Exchange
                ? new SwapSlot { ClassId = Input.SourceClassId, Date = Input.SourceDate ?? "", Index = Input.SourceIndex }
                : null,
        };
        return await RunAsync(async () =>
        {
            var created = await swaps.CreateAsync(CurrentUser.Id, request, ct);
            return created.Status switch
            {
                SwapRequestStatus.Forced => "已强制换课，并已通知对方老师。",
                SwapRequestStatus.Approved => "两节课都是你的课，已直接完成临时换课。",
                _ => "换课申请已发送，等待对方老师处理。",
            };
        });
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, string? note, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            await swaps.ApproveAsync(CurrentUser.Id, id, note, ct);
            return "已同意换课，课表已临时调整。";
        });
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, string? note, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            await swaps.RejectAsync(CurrentUser.Id, id, note, ct);
            return "已拒绝换课申请。";
        });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            await swaps.CancelAsync(CurrentUser.Id, id, ct);
            return "已撤销换课申请。";
        });
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            await swaps.RevokeForcedAsync(CurrentUser.Id, id, ct);
            return "已撤回强制换课，课表已恢复。";
        });
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action)
    {
        try
        {
            TempData["Message"] = await action();
        }
        catch (SwapOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Catalog = await swaps.GetCatalogAsync(CurrentUser.Id, ct);
        Incoming = await swaps.ListAsync(CurrentUser.Id, "incoming", null, ct);
        Outgoing = await swaps.ListAsync(CurrentUser.Id, "outgoing", null, ct);
    }

    public static string StatusText(SwapRequestStatus status) => status switch
    {
        SwapRequestStatus.Pending => "待审批",
        SwapRequestStatus.Approved => "已通过",
        SwapRequestStatus.Rejected => "已拒绝",
        SwapRequestStatus.Cancelled => "已撤销",
        SwapRequestStatus.Expired => "已过期",
        SwapRequestStatus.Forced => "已强制换课",
        SwapRequestStatus.Revoked => "已撤回",
        _ => status.ToString(),
    };

    public static string StatusClass(SwapRequestStatus status) => status switch
    {
        SwapRequestStatus.Pending => "swap-status pending",
        SwapRequestStatus.Approved => "swap-status ok",
        SwapRequestStatus.Forced => "swap-status forced",
        _ => "swap-status",
    };

    public static string SlotText(SwapSlot slot)
    {
        var date = DateOnly.TryParseExact(slot.Date, "yyyy-MM-dd", out var value)
            ? $"{value.Month}月{value.Day}日 周{"日一二三四五六"[(int)value.DayOfWeek]}"
            : slot.Date;
        var teacher = string.IsNullOrWhiteSpace(slot.Teacher) ? "" : $" · {slot.Teacher}";
        return $"{slot.ClassName} {date} {slot.Label} {slot.Subject}{teacher}";
    }

    public sealed class SwapInput
    {
        public SwapMode Mode { get; set; } = SwapMode.Exchange;
        public Guid SourceClassId { get; set; }
        public string? SourceDate { get; set; }
        public int SourceIndex { get; set; }
        public Guid TargetClassId { get; set; }
        public string? TargetDate { get; set; }
        public int TargetIndex { get; set; }
        public string? SubjectName { get; set; }
        public string? Reason { get; set; }
        public bool Force { get; set; }
    }
}
