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

    public int PendingIncoming => Incoming.Count(x => x.NeedsAction);

    [BindProperty] public SwapInput Input { get; set; } = new();

    /// <summary>上次审批返回了需要确认的冲突：对应申请卡片上列出冲突并提供“仍然通过”。</summary>
    public Guid? ConflictSwapId { get; private set; }
    public IReadOnlyList<SwapConflict> Conflicts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.RequestScheduleSwap) is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.RequestScheduleSwap) is { } denied) return denied;
        var request = BuildRequest();
        return await RunAsync(async () =>
        {
            var created = await swaps.CreateAsync(CurrentUser.Id, request, ct);
            var offline = created.Delivery == SwapDeliveryStates.Pending ? "教室电脑当前离线，上线后自动生效。" : "";
            return created.Status switch
            {
                SwapRequestStatus.Forced => "已强制换课，并已通知对方老师。" + offline,
                SwapRequestStatus.Approved => "两节课都是你的课，已直接完成临时换课。" + offline,
                _ => "换课申请已发送，等待对方老师处理。",
            };
        });
    }

    /// <summary>提交前预检冲突，由 swap-requests.js 调用；校验失败时返回错误文案。</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.RequestScheduleSwap) is not null) return Unauthorized();
        try
        {
            return new JsonResult(await swaps.PreviewAsync(CurrentUser.Id, BuildRequest(), ct), JsonDefaults.Options);
        }
        catch (SwapOperationException ex)
        {
            return new JsonResult(new ApiError { Code = ex.Code, Message = ex.Message }, JsonDefaults.Options) { StatusCode = ex.Status };
        }
    }

    private CreateSwapRequest BuildRequest() => new()
    {
        Mode = Input.Mode,
        Reason = Input.Reason ?? string.Empty,
        Force = Input.Force,
        AcknowledgeConflicts = Input.AcknowledgeConflicts,
        SubjectName = Input.Mode == SwapMode.Replace ? Input.SubjectName : null,
        Target = new SwapSlot { ClassId = Input.TargetClassId, Date = Input.TargetDate ?? "", Index = Input.TargetIndex },
        Source = Input.Mode == SwapMode.Exchange
            ? new SwapSlot { ClassId = Input.SourceClassId, Date = Input.SourceDate ?? "", Index = Input.SourceIndex }
            : null,
    };

    public async Task<IActionResult> OnPostApproveAsync(Guid id, string? note, bool acknowledgeConflicts, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            var approved = await swaps.ApproveAsync(CurrentUser.Id, id, note, acknowledgeConflicts, ct);
            return approved.Delivery == SwapDeliveryStates.Pending
                ? "已同意换课。教室电脑当前离线，上线后自动生效。"
                : "已同意换课，课表已临时调整。";
        }, id);
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

    public async Task<IActionResult> OnPostRevertAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return await RunAsync(async () =>
        {
            await swaps.RevertAsync(CurrentUser.Id, id, ct);
            return "已撤回换课，课表已恢复。";
        });
    }

    /// <summary>打开“待我处理”页签即看到了强制换课，视为已处理；返回剩余待处理数用于刷新角标。</summary>
    public async Task<IActionResult> OnPostSeenAsync(List<Guid> ids, CancellationToken ct)
    {
        if (await RequireAsync() is not null) return Unauthorized();
        if (ids.Count > 0) await swaps.MarkForcedSeenAsync(CurrentUser.Id, ids, ct);
        return new JsonResult(new { pending = await swaps.CountPendingForAsync(CurrentUser.Id, ct) });
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action, Guid? swapId = null)
    {
        try
        {
            TempData["Message"] = await action();
        }
        catch (SwapOperationException ex)
        {
            TempData["Error"] = ex.Message;
            if (swapId is { } id && ex.Conflicts is { Count: > 0 } conflicts)
            {
                TempData["SwapConflictId"] = id.ToString();
                TempData["SwapConflicts"] = JsonSerializer.Serialize(conflicts, JsonDefaults.Options);
                return RedirectToPage(null, null, "incoming");
            }
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (TempData["SwapConflictId"] is string conflictId && Guid.TryParse(conflictId, out var parsed) &&
            TempData["SwapConflicts"] is string conflictsJson)
        {
            ConflictSwapId = parsed;
            try { Conflicts = JsonSerializer.Deserialize<List<SwapConflict>>(conflictsJson, JsonDefaults.Options) ?? []; }
            catch (JsonException) { Conflicts = []; }
        }
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
        SwapRequestStatus.Invalidated => "已失效",
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
        public bool AcknowledgeConflicts { get; set; }
    }
}
