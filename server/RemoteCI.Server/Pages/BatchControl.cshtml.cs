using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 系统管理员的批量控制台：勾选班级/分组后，批量发送通知、语音消息、清除通知或执行电源操作。
/// 与“班级管理”一样属于管理员管理器，在导航中与单个班级的操作分区展示。
/// </summary>
[Authorize]
public sealed class BatchControlModel(
    UserManager<AppUser> users,
    ClassroomService classrooms,
    ClassBroadcastService broadcast,
    IdentityCoordinator identities) : WebPageModel(users)
{
    private const string ResultsKey = "BatchControlResults";

    [BindProperty]
    public string? BroadcastTitle { get; set; }

    [BindProperty]
    public string? BroadcastMessage { get; set; }

    [BindProperty]
    public List<Guid> SelectedClassIds { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedGroupIds { get; set; } = [];

    [BindProperty]
    public PowerActionKind PowerAction { get; set; }

    [BindProperty]
    public bool ConfirmPower { get; set; }

    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> Groups { get; private set; } = [];

    /// <summary>上一次批量操作逐班结果，从 TempData 恢复后展示。</summary>
    public IReadOnlyList<BatchClassItemResult> LastResults { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        await LoadAsync(ct);
        RestoreResults();
        return Page();
    }

    public async Task<IActionResult> OnPostNotifyAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!TargetsSelected()) return Back("请先勾选要通知的班级或分组。");
        var result = await DispatchAsync(new BroadcastCommandRequest
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest
            {
                Title = string.IsNullOrWhiteSpace(BroadcastTitle) ? "RemoteCI 通知" : BroadcastTitle.Trim(),
                Message = BroadcastMessage?.Trim() ?? string.Empty,
            },
        }, ct);
        SaveResults(result);
        TempData[Summary(result, out _) ? "Message" : "Error"] = Summarize(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!TargetsSelected()) return Back("请先勾选要操作的班级或分组。");
        var result = await DispatchAsync(new BroadcastCommandRequest { Command = CommandKind.ClearNotifications }, ct);
        SaveResults(result);
        TempData[Summary(result, out _) ? "Message" : "Error"] = Summarize(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPowerAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!TargetsSelected()) return Back("请先勾选要操作的班级或分组。");
        if (!ConfirmPower)
        {
            TempData["Error"] = "电源操作影响教室端设备，请先勾选确认后执行。";
            return RedirectToPage();
        }
        if (!Enum.IsDefined(PowerAction))
        {
            TempData["Error"] = "未知电源操作。";
            return RedirectToPage();
        }
        var result = await DispatchAsync(new BroadcastCommandRequest { Command = CommandKind.Power, PowerAction = PowerAction }, ct);
        SaveResults(result);
        TempData[Summary(result, out _) ? "Message" : "Error"] = Summarize(result);
        return RedirectToPage();
    }

    /// <summary>批量语音消息：voice-message.js 直接上传原始 PCM，目标班级经查询参数传递。</summary>
    public async Task<IActionResult> OnPostBroadcastVoiceAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (Request.ContentType != "application/octet-stream" || Request.ContentLength is > VoiceMessageRequest.MaxBytes)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒"));
        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (audio.Length + count > VoiceMessageRequest.MaxBytes)
                return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音不能超过 60 秒"));
            audio.Write(buffer, 0, count);
        }
        var classIds = Request.Query["classIds"]
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        var groupIds = Request.Query["groupIds"]
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        if (classIds.Count == 0 && groupIds.Count == 0)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "请先勾选要广播的班级或分组"));

        var result = await DispatchAsync(new BroadcastCommandRequest
        {
            Command = CommandKind.SendVoiceMessage,
            VoiceMessage = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(audio.ToArray()) },
            ClassIds = classIds,
            GroupIds = groupIds,
        }, ct);
        var ok = result.Results.Count(x => x.Success);
        var failures = result.Results.Where(x => !x.Success).ToList();
        var message = failures.Count == 0
            ? $"语音消息已广播到 {ok} 个班级。"
            : $"语音广播完成 {ok} 个班级，{failures.Count} 个失败：{string.Join("；", failures.Select(x => x.Message))}";
        SaveResults(result);
        return new JsonResult(new { success = ok > 0, message });
    }

    private async Task<BatchClassOperationResult> DispatchAsync(BroadcastCommandRequest request, CancellationToken ct)
    {
        request.ClassIds = SelectedClassIds;
        request.GroupIds = SelectedGroupIds;
        return await broadcast.BroadcastAsync(
            new AuthPrincipal(PeerRole.Watch,
                await identities.GetProfileAsync(CurrentUser.Id, ct), null, null, null),
            request, ct);
    }

    private bool TargetsSelected() => SelectedClassIds.Count > 0 || SelectedGroupIds.Count > 0;

    private IActionResult Back(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage();
    }

    private static bool Summary(BatchClassOperationResult result, out int ok)
    {
        ok = result.Results.Count(x => x.Success);
        return ok > 0;
    }

    private static string Summarize(BatchClassOperationResult result)
    {
        var ok = result.Results.Count(x => x.Success);
        var failures = result.Results.Where(x => !x.Success).ToList();
        return failures.Count == 0
            ? $"批量操作已完成（{ok} 个班级）。"
            : $"批量操作完成 {ok} 个，失败 {failures.Count} 个，详见下方逐班结果。";
    }

    private void SaveResults(BatchClassOperationResult result) =>
        TempData[ResultsKey] = JsonSerializer.Serialize(result.Results, JsonDefaults.Options);

    private void RestoreResults()
    {
        if (TempData[ResultsKey] is not string json) return;
        try
        {
            LastResults = JsonSerializer.Deserialize<List<BatchClassItemResult>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            LastResults = [];
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Classes = await classrooms.ListAsync(ct);
        Groups = await classrooms.ListGroupsAsync(ct);
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }
}
