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
    ClassroomService classrooms,
    DeviceInventoryService devices,
    PeerRegistry peers,
    IStateStore store,
    IdentityCoordinator identities,
    ExtensionPolicyService extensionPolicies,
    AuthorizationSyncService authorizationSync,
    ClassroomService classesService,
    ExtensionGroupService extensionGroups,
    ScreenshotDispatcher screenshotDispatcher,
    ScreenshotService screenshots) : BatchControlModel(users, classrooms, devices, identities)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    [BindProperty]
    public int VolumeLevel { get; set; }

    [BindProperty]
    public NoticeInput Input { get; set; } = new();

    [BindProperty]
    public List<ExtensionFieldInput> ExtensionInputs { get; set; } = [];

    [BindProperty]
    public string? ClassNameInput { get; set; }

    [BindProperty]
    public IFormFile? AvatarFile { get; set; }

    /// <summary>上传的主界面文件（ClassIsland Config/ComponentLayouts/*.json）。</summary>
    [BindProperty]
    public IFormFile? LayoutFile { get; set; }

    [BindProperty]
    public MainWindowLayoutApplyMode LayoutMode { get; set; } = MainWindowLayoutApplyMode.OverwriteCurrent;

    [BindProperty]
    public string? LayoutName { get; set; }

    /// <summary>新建后立即使用；未勾选的复选框不提交，因此默认值为 false，页面默认勾选。</summary>
    [BindProperty]
    public bool LayoutActivate { get; set; }

    public bool HasAvatar => CurrentClass?.HasAvatar == true;

    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    public bool GuardOnline => peers.HasGuardFor(CurrentClassId);
    public bool GuardOnly => GuardOnline && !PluginOnline;
    public bool CanLaunchClassIsland => CurrentUser.Role == UserRole.Admin &&
        ClassPermissions.HasFlag(UserPermissions.ManageUsers) &&
        peers.PrimaryGuardSupports(CurrentClassId, RemoteCiCapabilities.AppGuardLaunch);
    public ClassStateSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<ExtensionControlItem> Extensions { get; private set; } = [];

    /// <summary>当前班级扩展按插件分组后的视图；分组内的扩展只包含当前账号可见的项。</summary>
    public IReadOnlyList<ExtensionGroupView> ExtensionGroups { get; private set; } = [];

    /// <summary>当前账号在本班可自行管理的插件；系统管理员为 null，表示不受限。</summary>
    public IReadOnlySet<string>? EditableExtensionGroupIds { get; private set; }

    /// <summary>
    /// 当前账号能否修改本班该插件的设置：系统管理员；或该插件已开放班级自治、且在本班拥有扩展功能权限的班主任。
    /// 与 ExtensionGroupService.CanEditSettingsAsync 的口径一致。
    /// </summary>
    public bool CanEditExtensionSettings(ExtensionGroupView group) =>
        group.HasSettings && (EditableExtensionGroupIds is null
            ? ClassPermissions.HasFlag(UserPermissions.RunExtensions)
            : EditableExtensionGroupIds.Contains(group.Id));

    /// <summary>分组是否在控制页展示：有可见扩展，或当前账号可以打开它的设置页。</summary>
    public bool ShowExtensionGroup(ExtensionGroupView group) =>
        group.Extensions.Count > 0 || CanEditExtensionSettings(group);

    public ExtensionControlItem ExtensionItem(ExtensionDefinition definition) =>
        Extensions.First(item => string.Equals(item.Definition.Id, definition.Id, StringComparison.Ordinal));
    // “老师来了”暂时只保留协议处理以兼容旧客户端，不在 WebUI 暴露入口。
    public bool CanTeacherComing => ClassPermissions.HasFlag(UserPermissions.TeacherComing) && Supports(RemoteCiCapabilities.TeacherComing);
    public bool CanSendNotifications => ClassPermissions.HasFlag(UserPermissions.SendNotifications) && Supports(RemoteCiCapabilities.NotificationSend);
    public bool CanSendVoiceMessages => ClassPermissions.HasFlag(UserPermissions.SendVoiceMessages) && Supports(RemoteCiCapabilities.VoiceMessageSend);
    public bool CanClearNotifications => ClassPermissions.HasFlag(UserPermissions.SendNotifications) && Supports(RemoteCiCapabilities.NotificationClear);
    public bool CanControlMainMenu => ClassPermissions.HasFlag(UserPermissions.MainMenuControl) && Supports(RemoteCiCapabilities.MainMenuVisibility);
    public bool CanControlPower => ClassPermissions.HasFlag(UserPermissions.PowerControl) && Supports(RemoteCiCapabilities.PowerControl);
    public bool CanControlVolume => ClassPermissions.HasFlag(UserPermissions.PowerControl) && Supports(RemoteCiCapabilities.VolumeControl);
    public bool CanUseExtensions => ClassPermissions.HasFlag(UserPermissions.RunExtensions) && Supports(RemoteCiCapabilities.ExtensionsRun);
    public bool CanCaptureScreen => ClassPermissions.HasFlag(UserPermissions.CaptureScreen) && Supports(RemoteCiCapabilities.ScreenCapture);
    public bool CanUploadMainWindowLayout => ClassPermissions.HasFlag(UserPermissions.MainMenuControl) && Supports(RemoteCiCapabilities.MainWindowLayoutApply);
    public bool CanReadMainWindowLayout => ClassPermissions.HasFlag(UserPermissions.MainMenuControl) && Supports(RemoteCiCapabilities.MainWindowLayoutRead);
    public override bool IsSingleControl => true;

    public override async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } denied) return denied;
        RestoreResults();
        return Page();
    }

    /// <summary>截取当前班级教室电脑的屏幕；插件上传完成后返回截图信息（JSON）。</summary>
    public async Task<IActionResult> OnPostScreenshotAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ClassPermissions.HasFlag(UserPermissions.CaptureScreen))
            return new JsonResult(CommandResult.Failure(CommandResultCodes.Forbidden, "没有该班级的屏幕截图权限"));
        var requestedBy = (await Identities.GetProfileAsync(CurrentUser.Id, ct))?.WithPermissions(ClassPermissions);
        if (requestedBy is null) return new JsonResult(CommandResult.Failure(CommandResultCodes.Unauthorized, "登录已失效"));
        var outcome = await screenshotDispatcher.CaptureAsync(CurrentClassId, null, requestedBy, ct);
        return new JsonResult(new
        {
            success = outcome.Record is not null,
            message = outcome.Record is null ? outcome.Result.Message : "截图已获取",
            screenshot = outcome.Record is { } record ? ScreenshotPageView(Url, record) : null,
        });
    }

    /// <summary>当前班级保留期内的截图（新到旧）。</summary>
    public async Task<IActionResult> OnGetScreenshotsAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ClassPermissions.HasFlag(UserPermissions.CaptureScreen)) return new JsonResult(Array.Empty<object>());
        return new JsonResult(screenshots.ListForClass(CurrentClassId).Select(x => ScreenshotPageView(Url, x)));
    }

    /// <summary>截图 PNG：要求对截图所属班级拥有 CaptureScreen，不限于当前选中的班级。</summary>
    public async Task<IActionResult> OnGetScreenshotImageAsync(string id, bool download = false)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (screenshots.Get(id) is not { } record ||
            AccessibleClasses.FirstOrDefault(x => x.Id == record.ClassId)?.Permissions is not { } permissions ||
            !permissions.HasFlag(UserPermissions.CaptureScreen))
            return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        var path = screenshots.ContentPath(record.Id);
        return download
            ? PhysicalFile(path, "image/png", $"screenshot-{record.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss}.png")
            : PhysicalFile(path, "image/png");
    }

    /// <summary>网页端截图信息：图片走控制页的 Cookie 鉴权处理器，设备状态页共用。</summary>
    internal static object ScreenshotPageView(IUrlHelper url, ScreenshotRecord record) => new
    {
        id = record.Id,
        deviceName = record.DeviceName,
        requestedBy = record.RequestedByName,
        width = record.Width,
        height = record.Height,
        size = record.Size,
        capturedAt = record.CreatedAt,
        url = url.Page("/Control", "ScreenshotImage", new { id = record.Id }),
        downloadUrl = url.Page("/Control", "ScreenshotImage", new { id = record.Id, download = true }),
    };

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
            VoiceMessage = CreateVoiceRequest(audio.ToArray()),
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
                ForceSenderInTitle = await Identities.GetForceSenderInTitleAsync(ct),
                IsSpeechEnabled = Input.IsSpeechEnabled,
                IsNotificationSoundEnabled = Input.IsNotificationSoundEnabled,
                IsNotificationEffectEnabled = Input.IsNotificationEffectEnabled,
                IsNotificationTopmostEnabled = Input.IsNotificationTopmostEnabled,
                DurationSeconds = Input.DurationSeconds,
                RepeatCounts = Input.RepeatCounts,
                IsRollingEnabled = Input.IsRollingEnabled,
            },
        }, ct);
        return RedirectWithResult(result);
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

    /// <summary>上传主界面文件：覆盖设备当前使用的文件，或新建（可立即使用）。</summary>
    public async Task<IActionResult> OnPostMainWindowLayoutAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.MainMenuControl) is { } classDenied) return classDenied;
        if (LayoutFile is null || LayoutFile.Length == 0)
            return RedirectWithResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "请选择主界面文件"));
        if (LayoutFile.Length > MainWindowLayoutRules.MaxUtf8Bytes + 3)
            return RedirectWithResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "主界面文件不能超过 2 MB"));
        string json;
        using (var reader = new StreamReader(LayoutFile.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            json = await reader.ReadToEndAsync(ct);
        var request = new MainWindowLayoutApplyRequest
        {
            LayoutJson = json,
            Mode = LayoutMode,
            Name = LayoutMode == MainWindowLayoutApplyMode.CreateNew ? LayoutName : null,
            Activate = LayoutMode == MainWindowLayoutApplyMode.OverwriteCurrent || LayoutActivate,
        };
        // 与插件执行端同一套校验，提前反馈格式与文件名错误。
        if (MainWindowLayoutRules.Validate(request) is { } error)
            return RedirectWithResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, error));
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.ApplyMainWindowLayout,
            MainWindowLayout = request,
        }, ct));
    }

    /// <summary>获取设备当前使用的主界面文件并作为 JSON 下载。</summary>
    public async Task<IActionResult> OnPostDownloadMainWindowLayoutAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.MainMenuControl) is { } classDenied) return classDenied;
        var result = await SendAsync(new CommandMessage { Command = CommandKind.ReadMainWindowLayout }, ct);
        if (!result.Success) return RedirectWithResult(result);
        MainWindowLayoutSnapshot? snapshot = null;
        try { snapshot = System.Text.Json.JsonSerializer.Deserialize<MainWindowLayoutSnapshot>(result.Data ?? string.Empty); }
        catch (System.Text.Json.JsonException) { }
        if (snapshot is null || string.IsNullOrEmpty(snapshot.LayoutJson))
            return RedirectWithResult(CommandResult.Failure(CommandResultCodes.InternalError, "设备返回的主界面文件无效"));
        var fileName = MainWindowLayoutRules.NormalizeName(snapshot.Name, out var name) is null ? name : "主界面";
        return File(System.Text.Encoding.UTF8.GetBytes(snapshot.LayoutJson), "application/json", fileName + ".json");
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

    public async Task<IActionResult> OnPostRestartClassIslandAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin)
            return RedirectToPage("/Denied");
        if (RequireClass(UserPermissions.ManageUsers) is { } classDenied) return classDenied;
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.RestartClassIsland,
        }, ct));
    }

    public async Task<IActionResult> OnPostLaunchClassIslandAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        if (RequireClass(UserPermissions.ManageUsers) is { } classDenied) return classDenied;
        var requestedBy = (await Identities.GetProfileAsync(CurrentUser.Id, ct))?.WithPermissions(ClassPermissions);
        if (requestedBy is null) return RedirectToPage("/Login");
        return RedirectWithResult(await peers.LaunchClassIslandAsync(CurrentClassId, requestedBy, CommandTimeout, ct));
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

        var submitted = ExtensionFieldInput.ToValues(ExtensionInputs);
        var args = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters ?? [])
        {
            submitted.TryGetValue(parameter.Key, out var value);
            value ??= parameter.Type == ExtensionParameterType.Switch
                ? (string.Equals(parameter.DefaultValue, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false")
                : parameter.DefaultValue;
            args[parameter.Key] = value;
        }
        // 与插件执行端使用同一套校验，提前把类型、范围与必填错误反馈给用户。
        args = ExtensionFieldValidator.ValidateArguments(definition.Parameters ?? [], args, out var argumentError);
        if (argumentError is not null)
        {
            TempData["Error"] = argumentError;
            return RedirectToPage();
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

    // ---------- 班级设置（班名/头像）：系统管理员或本班班主任 ----------

    public async Task<IActionResult> OnPostClassInfoAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ClassSelfService.CanRename) return RedirectToPage("/Denied");
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
        if (!ClassSelfService.CanChangeAvatar) return RedirectToPage("/Denied");
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
        if (!ClassSelfService.CanChangeAvatar) return RedirectToPage("/Denied");
        await classesService.SetAvatarAsync(CurrentClassId, null, null, ct);
        TempData["Message"] = "班头像已清除。";
        return RedirectToPage();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await LoadBatchAsync(ct);
        Snapshot = store.GetLatestSnapshot(CurrentClassId);
        VolumeLevel = Snapshot?.VolumePercent ?? 0;
        Extensions = await extensionPolicies.ListForUserAsync(
            CurrentUser.Id,
            CurrentUser.Role,
            ClassPermissions,
            store.GetLatestExtensions(CurrentClassId) ?? [],
            ct);
        ExtensionGroups = extensionGroups.BuildForClass(
            CurrentClassId, Extensions.Select(item => item.Definition).ToList());
        EditableExtensionGroupIds = await extensionGroups.ListClassAdminEditableAsync(
            CurrentUser.Id, CurrentUser.Role, CurrentUser.GrantedPermissions, CurrentClassId, ct);
        return !MobileUi.IsMobile(HttpContext) && !CanTeacherComing && !CanSendNotifications && !CanSendVoiceMessages && !CanClearNotifications && !CanControlMainMenu &&
            !CanControlPower && !CanControlVolume && !CanUseExtensions && !HasMaintenanceOperations
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
        command.RequestedBy = (await Identities.GetProfileAsync(CurrentUser.Id, ct))?.WithPermissions(ClassPermissions);
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

        /// <summary>单条显示秒数；null 或 &lt;= 0 时插件按 ClassIsland 集控默认 5 秒处理。</summary>
        [Range(0, 3600)] public int? DurationSeconds { get; set; }

        /// <summary>重复次数；null 或 &lt; 1 时按 1 次处理。</summary>
        [Range(0, 10)] public int? RepeatCounts { get; set; }

        /// <summary>正文横向滚动显示；默认关闭。</summary>
        public bool IsRollingEnabled { get; set; }

        public bool IsSpeechEnabled { get; set; }
        public bool IsNotificationSoundEnabled { get; set; }
        public bool IsNotificationEffectEnabled { get; set; }

        /// <summary>提醒时置顶 ClassIsland 主界面。</summary>
        public bool IsNotificationTopmostEnabled { get; set; }
    }
}
