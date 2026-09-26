using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ControlModel(
    UserManager<AppUser> users,
    PeerRegistry peers,
    IStateStore store,
    IdentityCoordinator identities,
    ExtensionPolicyService extensionPolicies,
    AuthorizationSyncService authorizationSync,
    ClassBroadcastService broadcast,
    ClassroomService classesService) : WebPageModel(users)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    [BindProperty]
    public int VolumeLevel { get; set; }

    [BindProperty]
    public NoticeInput Input { get; set; } = new();

    [BindProperty]
    public bool ForceSenderInTitle { get; set; }

    [BindProperty]
    public List<ExtensionInput> ExtensionInputs { get; set; } = [];

    [BindProperty]
    public NoticeInput BroadcastInput { get; set; } = new();

    [BindProperty]
    public string? ClassNameInput { get; set; }

    [BindProperty]
    public IFormFile? AvatarFile { get; set; }

    public bool HasAvatar => CurrentClass?.HasAvatar == true;

    [BindProperty]
    public List<Guid> BroadcastClassIds { get; set; } = [];

    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    public ClassStateSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<ExtensionControlItem> Extensions { get; private set; } = [];
    public bool CanTeacherComing => ClassPermissions.HasFlag(UserPermissions.TeacherComing) && Supports(RemoteCiCapabilities.TeacherComing);
    public bool CanSendNotifications => ClassPermissions.HasFlag(UserPermissions.SendNotifications) && Supports(RemoteCiCapabilities.NotificationSend);
    public bool CanSendVoiceMessages => ClassPermissions.HasFlag(UserPermissions.SendVoiceMessages) && Supports(RemoteCiCapabilities.VoiceMessageSend);
    public bool CanClearNotifications => ClassPermissions.HasFlag(UserPermissions.SendNotifications) && Supports(RemoteCiCapabilities.NotificationClear);
    public bool CanControlMainMenu => ClassPermissions.HasFlag(UserPermissions.MainMenuControl) && Supports(RemoteCiCapabilities.MainMenuVisibility);
    public bool CanControlPower => ClassPermissions.HasFlag(UserPermissions.PowerControl) && Supports(RemoteCiCapabilities.PowerControl);
    public bool CanControlVolume => ClassPermissions.HasFlag(UserPermissions.PowerControl) && Supports(RemoteCiCapabilities.VolumeControl);
    public bool CanUseExtensions => ClassPermissions.HasFlag(UserPermissions.RunExtensions) && Supports(RemoteCiCapabilities.ExtensionsRun);
    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;

    /// <summary>广播面板的候选班级：当前用户有通知发送权限的班级（不含当前班——单班用上方表单即可）。</summary>
    public IReadOnlyList<ClassSummary> BroadcastTargets { get; private set; } = [];

    /// <summary>有至少两个可广播班级时才展示广播面板，单班级部署保持原有操作路径。</summary>
    public bool ShowBroadcast => BroadcastTargets.Count >= 2;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } denied) return denied;
        return Page();
    }

    public async Task<IActionResult> OnPostTeacherComingAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.TeacherComing) is { } classDenied) return classDenied;
        return RedirectWithResult(await SendAsync(
            new CommandMessage { Command = CommandKind.TeacherComing }, ct));
    }

    public async Task<IActionResult> OnPostVoiceMessageAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.SendVoiceMessages) is { } classDenied) return classDenied;
        // 原始 PCM 请求不经过表单文件缓存，不在服务器临时目录保留录音。
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
        if (audio.Length < 2 || audio.Length % 2 != 0)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "没有有效录音，请重新录制"));
        return new JsonResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage,
            VoiceMessage = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(audio.ToArray()) },
        }, ct));
    }

    public async Task<IActionResult> OnPostNotificationAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.SendNotifications) is { } classDenied) return classDenied;
        if (!ModelState.IsValid)
        {
            if (await LoadAsync(ct) is { } loadDenied) return loadDenied;
            return Page();
        }

        var result = await SendAsync(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest
            {
                Title = string.IsNullOrWhiteSpace(Input.Title) ? "RemoteCI 通知" : Input.Title.Trim(),
                Message = Input.Message?.Trim() ?? string.Empty,
                ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct),
            },
        }, ct);
        return RedirectWithResult(result);
    }

    public async Task<IActionResult> OnPostNotificationSettingsAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.SendNotifications) is { } classDenied) return classDenied;
        var settings = await identities.SetForceSenderInTitleAsync(ForceSenderInTitle, ct);
        await peers.SendSettingsToWatchesAsync(settings, ct);
        TempData["Message"] = ForceSenderInTitle ? "已开启强制显示发送人" : "已关闭强制显示发送人";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearNotificationsAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.SendNotifications) is { } classDenied) return classDenied;
        return RedirectWithResult(await SendAsync(
            new CommandMessage { Command = CommandKind.ClearNotifications }, ct));
    }

    public async Task<IActionResult> OnPostMainMenuAsync(bool visible, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.MainMenuControl) is { } classDenied) return classDenied;
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.SetMainMenuVisibility,
            MainMenuVisible = visible,
        }, ct));
    }

    public async Task<IActionResult> OnPostVolumeAsync(bool unmute, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.PowerControl) is { } classDenied) return classDenied;
        if (VolumeLevel is < 0 or > 100)
            return VolumeResult(CommandResult.Failure(
                CommandResultCodes.InvalidRequest, "音量必须在 0 到 100 之间"));

        var result = await SendAsync(new CommandMessage
        {
            Command = CommandKind.Volume,
            // 静音状态下向高调节时，把取消静音与音量变更合并为同一条插件命令。
            Volume = CreateVolumeRequest(VolumeLevel, unmute),
        }, ct);
        return VolumeResult(result, unmute);
    }

    public async Task<IActionResult> OnPostMuteAsync(bool muted, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.PowerControl) is { } classDenied) return classDenied;
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.Volume,
            Volume = new VolumeControlRequest { Muted = muted },
        }, ct));
    }

    public async Task<IActionResult> OnPostPowerAsync(PowerActionKind action, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.PowerControl) is { } classDenied) return classDenied;
        if (!Enum.IsDefined(action))
        {
            TempData["Error"] = "未知电源操作";
            return RedirectToPage();
        }
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.Power,
            PowerAction = action,
        }, ct));
    }

    public async Task<IActionResult> OnPostExtensionAsync(string extensionId, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.RunExtensions) is { } classDenied) return classDenied;
        var definition = store.GetLatestExtensions(CurrentClassId)?.FirstOrDefault(extension =>
            string.Equals(extension.Id, extensionId, StringComparison.Ordinal));
        if (definition is null)
        {
            TempData["Error"] = "扩展功能不存在或尚未同步";
            return RedirectToPage();
        }
        var item = (await extensionPolicies.ListForUserAsync(
                CurrentUser.Id, CurrentUser.Role, ClassPermissions, [definition], ct))
            .SingleOrDefault();
        if (item?.CanInvoke != true) return RedirectToPage("/Denied");

        var submitted = ExtensionInputs
            .Where(input => !string.IsNullOrWhiteSpace(input.Key))
            .GroupBy(input => input.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var args = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters ?? [])
        {
            submitted.TryGetValue(parameter.Key, out var value);
            value ??= parameter.Type == ExtensionParameterType.Switch
                ? (string.Equals(parameter.DefaultValue, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false")
                : parameter.DefaultValue;
            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                TempData["Error"] = $"请填写“{parameter.Label}”";
                return RedirectToPage();
            }
            args[parameter.Key] = value;
        }

        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = definition.Id,
            ExtensionArgs = args,
        }, ct));
    }

    public async Task<IActionResult> OnPostExtensionPolicyAsync(
        string extensionId,
        bool enabled,
        bool allowNonAdmin,
        bool showOnWatch,
        CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        if (!HasCurrentExtension(extensionId))
        {
            TempData["Error"] = "扩展功能不存在或尚未同步";
            return RedirectToPage();
        }

        try
        {
            await extensionPolicies.UpdateAdminAsync(
                CurrentUser.Id, extensionId, enabled, allowNonAdmin, showOnWatch, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "扩展功能设置已保存。";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExtensionPreferenceAsync(
        string extensionId,
        bool showOnWatch,
        CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.RunExtensions) is { } classDenied) return classDenied;
        var definition = store.GetLatestExtensions(CurrentClassId)?.FirstOrDefault(x => x.Id == extensionId);
        if (definition is null) return RedirectToPage("/Denied");

        try
        {
            await extensionPolicies.UpdatePersonalAsync(CurrentUser.Id, extensionId, showOnWatch, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "自己的手表展示设置已保存。";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    // ---------- 班级设置（班名/头像）：系统管理员或本班班管理员 ----------

    public async Task<IActionResult> OnPostClassInfoAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!CanManageClassInfo) return RedirectToPage("/Denied");
        if (string.IsNullOrWhiteSpace(ClassNameInput))
        {
            TempData["Error"] = "班名不能为空。";
            return RedirectToPage();
        }
        try
        {
            await classesService.RenameClassAsync(CurrentClassId, ClassNameInput, ct);
            TempData["Message"] = "班名已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAvatarAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!CanManageClassInfo) return RedirectToPage("/Denied");
        var allowed = new[] { "image/png", "image/jpeg", "image/webp" };
        if (AvatarFile is null || AvatarFile.Length is 0 or > 256 * 1024)
        {
            TempData["Error"] = "头像需为不超过 256KB 的图片。";
            return RedirectToPage();
        }
        if (!allowed.Contains(AvatarFile.ContentType))
        {
            TempData["Error"] = "头像仅支持 PNG/JPEG/WebP。";
            return RedirectToPage();
        }
        using var memory = new MemoryStream();
        await AvatarFile.CopyToAsync(memory, ct);
        await classesService.SetAvatarAsync(CurrentClassId, memory.ToArray(), AvatarFile.ContentType, ct);
        TempData["Message"] = "班头像已更新。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAvatarAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!CanManageClassInfo) return RedirectToPage("/Denied");
        await classesService.SetAvatarAsync(CurrentClassId, null, null, ct);
        TempData["Message"] = "班头像已清除。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBroadcastAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.SendNotifications) is { } classDenied) return classDenied;
        if (BroadcastClassIds.Count == 0)
        {
            TempData["Error"] = "请先勾选要通知的班级。";
            return RedirectToPage();
        }

        var result = await broadcast.BroadcastAsync(
            new AuthPrincipal(PeerRole.Watch,
                await identities.GetProfileAsync(CurrentUser.Id, ct),
                null, null, null),
            new BroadcastCommandRequest
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest
                {
                    Title = BroadcastInput.Title,
                    Message = BroadcastInput.Message,
                },
                ClassIds = BroadcastClassIds,
            }, ct);
        var ok = result.Results.Count(x => x.Success);
        var failures = result.Results.Where(x => !x.Success).ToList();
        TempData[ok > 0 ? "Message" : "Error"] = failures.Count == 0
            ? $"广播通知已发送到 {ok} 个班级。"
            : $"广播完成 {ok} 个班级，{failures.Count} 个失败：{string.Join("；", failures.Select(x => x.Message))}";
        return RedirectToPage();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Snapshot = store.GetLatestSnapshot(CurrentClassId);
        VolumeLevel = Snapshot?.VolumePercent ?? 0;
        if (CanSendNotifications) ForceSenderInTitle = await identities.GetForceSenderInTitleAsync();
        // 广播候选：有通知权限的可访问班级，排除当前班级（当前班用上方单班表单）。
        BroadcastTargets = AccessibleClasses
            .Where(x => x.Id != CurrentClassId && x.Permissions?.HasFlag(UserPermissions.SendNotifications) == true)
            .OrderBy(x => x.Name)
            .ToList();
        Extensions = await extensionPolicies.ListForUserAsync(
            CurrentUser.Id,
            CurrentUser.Role,
            ClassPermissions,
            store.GetLatestExtensions(CurrentClassId) ?? [],
            ct);
        return !CanTeacherComing && !CanSendNotifications && !CanSendVoiceMessages && !CanClearNotifications && !CanControlMainMenu &&
            !CanControlPower && !CanControlVolume && !CanUseExtensions
            ? RedirectToPage("/Denied")
            : null;
    }

    internal static VolumeControlRequest CreateVolumeRequest(int level, bool unmute) => new()
    {
        Level = level,
        Muted = unmute ? false : null,
    };

    private async Task<CommandResult> SendAsync(CommandMessage command, CancellationToken ct)
    {
        command.RequestedBy = await identities.GetProfileAsync(CurrentUser.Id, ct);
        command.ClassId = CurrentClassId;
        return await peers.SendCommandAndWaitAsync(command, CurrentClassId, CommandTimeout, ct);
    }

    private bool HasCurrentExtension(string extensionId) =>
        store.GetLatestExtensions(CurrentClassId)?.Any(x => x.Id == extensionId) == true;

    private bool Supports(string capability) =>
        !PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, capability);

    private IActionResult VolumeResult(CommandResult result, bool unmuted = false)
    {
        if (!string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            return RedirectWithResult(result);
        return new JsonResult(new
        {
            success = result.Success,
            message = result.Message,
            volumeLevel = VolumeLevel,
            unmuted = result.Success && unmuted,
        });
    }

    private IActionResult RedirectWithResult(CommandResult result, string? successMessage = null)
    {
        TempData[result.Success ? "Message" : "Error"] = result.Success && !string.IsNullOrWhiteSpace(successMessage)
            ? successMessage
            : result.Message;
        return RedirectToPage();
    }

    public sealed class NoticeInput
    {
        [StringLength(60)] public string? Title { get; set; }
        [StringLength(500)] public string? Message { get; set; }
    }

    public sealed class ExtensionInput
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
