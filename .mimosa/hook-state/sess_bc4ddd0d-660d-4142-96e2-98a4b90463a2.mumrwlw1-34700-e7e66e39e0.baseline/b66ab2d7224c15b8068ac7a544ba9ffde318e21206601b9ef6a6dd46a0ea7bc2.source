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
/// 系统管理员的批量控制台：先选择统一功能，再填写该功能的参数，最后选择班级、分组或具体设备。
/// 页面只负责收集参数；实际投递由 DeviceInventoryService 统一按插件连接执行。
/// </summary>
[Authorize]
public class BatchControlModel(
    UserManager<AppUser> users,
    ClassroomService classrooms,
    DeviceInventoryService devices,
    IdentityCoordinator identities) : WebPageModel(users)
{
    private const string ResultsKey = "BatchControlResults";
    protected static readonly TimeSpan BatchCommandTimeout = TimeSpan.FromSeconds(20);
    private const int MaxProfileJsonLength = 5 * 1024 * 1024;
    private const int MaxManagementPresetJsonLength = 256 * 1024;

    [BindProperty]
    public BatchOperationKind Operation { get; set; }

    [BindProperty]
    public string? BroadcastTitle { get; set; }

    [BindProperty]
    public string? BroadcastMessage { get; set; }

    /// <summary>通知单条显示秒数；null 或 &lt;= 0 时插件按 ClassIsland 集控默认 5 秒处理。</summary>
    [BindProperty]
    public int? BroadcastDurationSeconds { get; set; }

    /// <summary>正文滚动重复次数；null 或 &lt; 1 时按 1 次处理。</summary>
    [BindProperty]
    public int? BroadcastRepeatCounts { get; set; }

    [BindProperty]
    public bool BroadcastIsSpeechEnabled { get; set; }

    [BindProperty]
    public bool BroadcastIsSoundEnabled { get; set; }

    [BindProperty]
    public bool BroadcastIsEffectEnabled { get; set; }

    /// <summary>提醒时置顶 ClassIsland 主界面。</summary>
    [BindProperty]
    public bool BroadcastIsTopmostEnabled { get; set; }

    [BindProperty]
    public PowerActionKind PowerAction { get; set; }

    [BindProperty]
    public bool ConfirmPower { get; set; }

    [BindProperty]
    public bool ConfirmHighRisk { get; set; }

    [BindProperty]
    public PluginActionKind PluginAction { get; set; }

    [BindProperty]
    public List<string> PluginIds { get; set; } = [];

    [BindProperty]
    public string? PluginIdsText { get; set; }

    [BindProperty]
    public string? TimeLayoutName { get; set; }

    [BindProperty]
    public Guid? TimeLayoutId { get; set; }

    [BindProperty]
    public bool TimeLayoutActivate { get; set; }

    [BindProperty]
    public string? TimeLayoutPointsJson { get; set; }

    [BindProperty]
    public string? ProfileJson { get; set; }

    [BindProperty]
    public ProfileDistributionSection ProfileSections { get; set; }

    [BindProperty]
    public bool ProfileReplaceExisting { get; set; }

    /// <summary>管理员上传的 ClassIsland 集控配置文件（ManagementPreset.json）内容。</summary>
    [BindProperty]
    public string? ManagementPresetJson { get; set; }

    [BindProperty]
    public bool AllowRemotePluginInstall { get; set; } = true;

    [BindProperty]
    public bool AllowRemotePluginUninstall { get; set; } = true;

    [BindProperty]
    public bool Force { get; set; }

    [BindProperty]
    public List<Guid> SelectedClassIds { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedGroupIds { get; set; } = [];

    [BindProperty]
    public List<Guid> SelectedConnectionIds { get; set; } = [];

    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> Groups { get; private set; } = [];
    public IReadOnlyList<DeviceInventory> Devices { get; private set; } = [];
    public IReadOnlyList<KnownPlugin> KnownPlugins { get; private set; } = [];
    public IReadOnlyList<BatchDeviceItemResult> LastResults { get; protected set; } = [];
    protected ClassroomService Classrooms { get; } = classrooms;
    protected DeviceInventoryService DevicesService { get; } = devices;
    protected IdentityCoordinator Identities { get; } = identities;
    public virtual bool IsSingleControl => false;

    /// <summary>系统管理员：批量页整体只对管理员开放，单班页用它决定高风险操作是否可见。</summary>
    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;

    /// <summary>
    /// 卡片是否对该用户可见。批量控制页本身已限定管理员，因此全部显示；
    /// 单班控制页按协议权限表判断，避免出现看得见却执行不了（或被拒绝）的操作。
    /// </summary>
    public bool CanUseOperation(BatchOperationKind operation)
    {
        if (!IsSingleControl) return true;
        if (CommandForOperation(operation) is not { } command) return false;
        var required = CommandPermissions.Required(command);
        return required != UserPermissions.None && ClassPermissions.HasFlag(required);
    }

    /// <summary>是否存在任一可见操作：无权限时连目标选择弹窗也不渲染，避免把设备清单泄露到页面。</summary>
    public bool HasAnyOperations => HasClassroomOperations || HasProfileOperations || HasSoftwareOperations;

    /// <summary>分组可见性：某一组全部操作都无权限时整组隐藏，避免出现空面板。</summary>
    public bool HasClassroomOperations =>
        CanUseOperation(BatchOperationKind.Notify) ||
        CanUseOperation(BatchOperationKind.ClearNotifications) ||
        CanUseOperation(BatchOperationKind.Power) ||
        CanUseOperation(BatchOperationKind.VoiceMessage);

    public bool HasProfileOperations =>
        CanUseOperation(BatchOperationKind.UpdateTimeLayout) ||
        CanUseOperation(BatchOperationKind.DistributeProfile) ||
        CanUseOperation(BatchOperationKind.JoinManagement) ||
        CanUseOperation(BatchOperationKind.SetPluginManagementPolicy);

    public bool HasSoftwareOperations =>
        CanUseOperation(BatchOperationKind.InstallPlugins) ||
        CanUseOperation(BatchOperationKind.UninstallPlugins) ||
        CanUseOperation(BatchOperationKind.SetPluginEnabled) ||
        CanUseOperation(BatchOperationKind.RefreshSoftwareInventory) ||
        CanUseOperation(BatchOperationKind.UpgradePlugins) ||
        CanUseOperation(BatchOperationKind.UpgradeClassIsland) ||
        CanUseOperation(BatchOperationKind.RestartClassIsland);

    /// <summary>批量操作对应的协议命令，用于复用 CommandPermissions 的权限口径。</summary>
    private static CommandKind? CommandForOperation(BatchOperationKind operation) => operation switch
    {
        BatchOperationKind.Notify => CommandKind.SendNotification,
        BatchOperationKind.ClearNotifications => CommandKind.ClearNotifications,
        BatchOperationKind.Power => CommandKind.Power,
        BatchOperationKind.VoiceMessage => CommandKind.SendVoiceMessage,
        BatchOperationKind.UpdateTimeLayout => CommandKind.UpdateTimeLayout,
        BatchOperationKind.DistributeProfile => CommandKind.DistributeProfile,
        BatchOperationKind.InstallPlugins => CommandKind.InstallPlugins,
        BatchOperationKind.UninstallPlugins => CommandKind.UninstallPlugins,
        BatchOperationKind.SetPluginEnabled => CommandKind.SetPluginEnabled,
        BatchOperationKind.SetPluginManagementPolicy => CommandKind.SetPluginManagementPolicy,
        BatchOperationKind.RefreshSoftwareInventory => CommandKind.RefreshSoftwareInventory,
        BatchOperationKind.UpgradePlugins => CommandKind.UpgradePlugins,
        BatchOperationKind.UpgradeClassIsland => CommandKind.UpgradeClassIsland,
        BatchOperationKind.RestartClassIsland => CommandKind.RestartClassIsland,
        BatchOperationKind.JoinManagement => CommandKind.JoinManagement,
        _ => null,
    };

    public virtual async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBatchAsync(ct);
        RestoreResults();
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        await LoadBatchAsync(ct);
        if (!TargetsSelected()) return Back("请先选择要控制的班级、分组或设备。");
        if (RequiresRiskConfirmation(Operation) && !ConfirmHighRisk)
            return Back("该操作会影响教室端设备，请先勾选确认后执行。");

        try
        {
            var plan = await DevicesService.ResolveAsync(SelectedClassIds, SelectedGroupIds, SelectedConnectionIds, ct);
            if (plan.Targets.Count == 0 && plan.Failures.Count == 0)
                return Back("没有找到可用的在线设备。");

            var commandFactory = await BuildCommandFactoryAsync(ct);
            var dispatchResults = await DevicesService.DispatchAsync(
                plan.Targets,
                commandFactory,
                OperationName(Operation),
                BatchCommandTimeout,
                ct);
            var results = plan.Failures
                .Select(ToItemResult)
                .Concat(dispatchResults.Select(ToItemResult))
                .ToList();
            if (Operation == BatchOperationKind.RefreshSoftwareInventory && results.Any(x => x.Success))
                await Task.Delay(200, ct);

            SaveResults(results);
            TempData[results.Any(x => x.Success) ? "Message" : "Error"] = Summarize(results);
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            return Back(ex.Message);
        }
    }

    /// <summary>班级控制页复用批量命令构造，但目标固定为当前班级。</summary>
    public async Task<IActionResult> OnPostSingleExecuteAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!IsSingleControl) return RedirectToPage("/Denied");
        if (!CanExecuteInCurrentClass(Operation)) return RedirectToPage("/Denied");
        if (RequiresRiskConfirmation(Operation) && !ConfirmHighRisk)
            return SingleBack("该操作会影响教室端设备，请先勾选确认后执行。");

        try
        {
            var plan = await DevicesService.ResolveAsync([CurrentClassId], [], [], ct);
            if (plan.Targets.Count == 0 && plan.Failures.Count == 0)
                return SingleBack("当前班级没有在线设备。");
            var results = plan.Failures
                .Select(ToItemResult)
                .Concat((await DevicesService.DispatchAsync(
                    plan.Targets,
                    await BuildCommandFactoryAsync(ct),
                    OperationName(Operation),
                    BatchCommandTimeout,
                    ct)).Select(ToItemResult))
                .ToList();
            TempData[results.Any(x => x.Success) ? "Message" : "Error"] = Summarize(results);
            return RedirectToPage("/Control");
        }
        catch (InvalidOperationException ex)
        {
            return SingleBack(ex.Message);
        }
    }

    /// <summary>单班执行前复核：与卡片可见性使用同一权限口径，防止绕过前端直接提交。</summary>
    private bool CanExecuteInCurrentClass(BatchOperationKind operation) =>
        IsSingleControl && CanUseOperation(operation);

    private IActionResult SingleBack(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage("/Control");
    }

    /// <summary>语音消息沿用原始 PCM 上传；目标由共享选择弹窗写入查询参数。</summary>
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

        var classIds = ParseGuidQuery("classIds");
        var groupIds = ParseGuidQuery("groupIds");
        var connectionIds = ParseGuidQuery("connectionIds");
        if (classIds.Count == 0 && groupIds.Count == 0 && connectionIds.Count == 0)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "请先选择要广播的设备、班级或分组"));

        var plan = await DevicesService.ResolveAsync(classIds, groupIds, connectionIds, ct);
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        var dispatchResults = await DevicesService.DispatchAsync(
            plan.Targets,
            _ => new CommandMessage
            {
                Command = CommandKind.SendVoiceMessage,
                VoiceMessage = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(audio.ToArray()) },
                RequestedBy = profile,
            },
            "语音广播",
            BatchCommandTimeout,
            ct);
        var results = plan.Failures.Select(ToItemResult).Concat(dispatchResults.Select(ToItemResult)).ToList();
        SaveResults(results);
        var ok = results.Count(x => x.Success);
        var message = results.Count == 0 || ok == results.Count
            ? $"语音消息已发送到 {ok} 台设备。"
            : $"语音广播完成 {ok} 台，失败 {results.Count - ok} 台：{string.Join("；", results.Where(x => !x.Success).Select(x => $"{x.TargetName}：{x.Message}"))}";
        return new JsonResult(new { success = ok > 0, message });
    }

    public async Task<IActionResult> OnPostSingleVoiceAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!IsSingleControl || !ClassPermissions.HasFlag(UserPermissions.SendVoiceMessages))
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "没有语音消息权限"));
        if (Request.ContentType != "application/octet-stream" || Request.ContentLength is > VoiceMessageRequest.MaxBytes)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒"));
        using var audio = new MemoryStream();
        await Request.Body.CopyToAsync(audio, ct);
        if (audio.Length < 2 || audio.Length % 2 != 0)
            return new JsonResult(CommandResult.Failure(CommandResultCodes.InvalidRequest, "没有有效录音，请重新录制"));
        var plan = await DevicesService.ResolveAsync([CurrentClassId], [], [], ct);
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        var results = plan.Failures.Select(ToItemResult).Concat((await DevicesService.DispatchAsync(
            plan.Targets,
            _ => new CommandMessage
            {
                Command = CommandKind.SendVoiceMessage,
                VoiceMessage = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(audio.ToArray()) },
                RequestedBy = profile,
            },
            "语音消息",
            BatchCommandTimeout,
            ct)).Select(ToItemResult)).ToList();
        var ok = results.Count(x => x.Success);
        return new JsonResult(new { success = ok > 0, message = $"语音消息已发送到当前班级 {ok} 台设备。" });
    }

    protected async Task<Func<DeviceInventory, CommandMessage>> BuildCommandFactoryAsync(CancellationToken ct)
    {
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        var forceSender = await Identities.GetForceSenderInTitleAsync(ct);
        return Operation switch
        {
            BatchOperationKind.Notify => _ => new CommandMessage
            {
                Command = CommandKind.SendNotification,
                Notification = new NotificationRequest
                {
                    Title = string.IsNullOrWhiteSpace(BroadcastTitle) ? "RemoteCI 通知" : BroadcastTitle.Trim(),
                    Message = BroadcastMessage?.Trim() ?? string.Empty,
                    ForceSenderInTitle = forceSender,
                    IsSpeechEnabled = BroadcastIsSpeechEnabled,
                    IsNotificationSoundEnabled = BroadcastIsSoundEnabled,
                    IsNotificationEffectEnabled = BroadcastIsEffectEnabled,
                    IsNotificationTopmostEnabled = BroadcastIsTopmostEnabled,
                    DurationSeconds = BroadcastDurationSeconds,
                    RepeatCounts = BroadcastRepeatCounts,
                },
                RequestedBy = profile,
            },
            BatchOperationKind.ClearNotifications => _ => new CommandMessage
            {
                Command = CommandKind.ClearNotifications,
                RequestedBy = profile,
            },
            BatchOperationKind.Power => _ => new CommandMessage
            {
                Command = CommandKind.Power,
                PowerAction = BuildPowerAction(),
                RequestedBy = profile,
            },
            BatchOperationKind.UpdateTimeLayout => _ => new CommandMessage
            {
                Command = CommandKind.UpdateTimeLayout,
                TimeLayoutUpdate = BuildTimeLayoutRequest(),
                RequestedBy = profile,
            },
            BatchOperationKind.DistributeProfile => _ => new CommandMessage
            {
                Command = CommandKind.DistributeProfile,
                ProfileDistribution = BuildProfileDistributionRequest(),
                RequestedBy = profile,
            },
            BatchOperationKind.InstallPlugins => _ => new CommandMessage
            {
                Command = CommandKind.InstallPlugins,
                PluginManagement = BuildPluginRequest(PluginActionKind.Install),
                RequestedBy = profile,
            },
            BatchOperationKind.UninstallPlugins => _ => new CommandMessage
            {
                Command = CommandKind.UninstallPlugins,
                PluginManagement = BuildPluginRequest(PluginActionKind.Uninstall),
                RequestedBy = profile,
            },
            BatchOperationKind.SetPluginEnabled => _ => new CommandMessage
            {
                Command = CommandKind.SetPluginEnabled,
                PluginManagement = BuildPluginToggleRequest(),
                RequestedBy = profile,
            },
            BatchOperationKind.SetPluginManagementPolicy => _ => new CommandMessage
            {
                Command = CommandKind.SetPluginManagementPolicy,
                PluginManagementPolicy = new PluginManagementPolicyRequest
                {
                    AllowRemoteInstall = AllowRemotePluginInstall,
                    AllowRemoteUninstall = AllowRemotePluginUninstall,
                },
                RequestedBy = profile,
            },
            BatchOperationKind.RefreshSoftwareInventory => _ => new CommandMessage
            {
                Command = CommandKind.RefreshSoftwareInventory,
                RequestedBy = profile,
            },
            BatchOperationKind.UpgradePlugins => _ => new CommandMessage
            {
                Command = CommandKind.UpgradePlugins,
                SoftwareUpgrade = new SoftwareUpgradeRequest { Force = Force },
                RequestedBy = profile,
            },
            BatchOperationKind.UpgradeClassIsland => _ => new CommandMessage
            {
                Command = CommandKind.UpgradeClassIsland,
                SoftwareUpgrade = new SoftwareUpgradeRequest { Force = Force },
                RequestedBy = profile,
            },
            BatchOperationKind.RestartClassIsland => _ => new CommandMessage
            {
                Command = CommandKind.RestartClassIsland,
                RequestedBy = profile,
            },
            BatchOperationKind.JoinManagement => device => new CommandMessage
            {
                Command = CommandKind.JoinManagement,
                ManagementJoin = BuildManagementJoinRequest(device),
                RequestedBy = profile,
            },
            _ => throw new InvalidOperationException("不支持的批量操作。"),
        };
    }

    private PowerActionKind BuildPowerAction()
    {
        if (!Enum.IsDefined(PowerAction))
            throw new InvalidOperationException("未知电源操作。");
        return PowerAction;
    }

    private PluginManagementRequest BuildPluginToggleRequest()
    {
        if (PluginAction is not PluginActionKind.Enable and not PluginActionKind.Disable)
            throw new InvalidOperationException("请选择启用或禁用插件。");
        return BuildPluginRequest(PluginAction);
    }

    private PluginManagementRequest BuildPluginRequest(PluginActionKind action)
    {
        var ids = PluginIds
            .Concat((PluginIdsText ?? string.Empty)
                .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("请至少选择一个插件，或填写插件 Id。");
        if (action is not PluginActionKind.Install and not PluginActionKind.Uninstall and not PluginActionKind.Enable and not PluginActionKind.Disable)
            throw new InvalidOperationException("未知插件操作。");
        return new PluginManagementRequest
        {
            Action = action,
            PluginIds = ids,
            RestartAfter = true,
        };
    }

    private TimeLayoutUpdateRequest BuildTimeLayoutRequest()
    {
        if (string.IsNullOrWhiteSpace(TimeLayoutPointsJson))
            throw new InvalidOperationException("请至少填写一个时间点。");
        List<TimeLayoutPointRequest>? points;
        try
        {
            points = JsonSerializer.Deserialize<List<TimeLayoutPointRequest>>(TimeLayoutPointsJson, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("时间点数据格式无效，请重新填写。");
        }
        if (points is null || points.Count == 0)
            throw new InvalidOperationException("请至少填写一个时间点。");
        return new TimeLayoutUpdateRequest
        {
            TimeLayoutId = TimeLayoutId,
            Name = string.IsNullOrWhiteSpace(TimeLayoutName) ? "RemoteCI 时间表" : TimeLayoutName.Trim(),
            Activate = TimeLayoutActivate,
            Points = points,
            RestartAfter = true,
        };
    }

    private ProfileDistributionRequest BuildProfileDistributionRequest()
    {
        if (string.IsNullOrWhiteSpace(ProfileJson))
            throw new InvalidOperationException("请粘贴或上传 ClassIsland 档案 JSON。");
        if (ProfileJson.Length > MaxProfileJsonLength)
            throw new InvalidOperationException("档案 JSON 不能超过 5 MB。");
        if (ProfileSections == ProfileDistributionSection.None)
            throw new InvalidOperationException("请至少选择一个分发内容。");
        return new ProfileDistributionRequest
        {
            ProfileJson = ProfileJson,
            Sections = ProfileSections,
            ReplaceExisting = ProfileReplaceExisting,
            RestartAfter = true,
        };
    }

    /// <summary>
    /// 用管理员上传的集控配置文件构造加入请求；ID（ClassIdentity）按目标设备所属班级名自动填充。
    /// 只校验 JSON 结构与大小，具体字段由插件按 ClassIsland 的 ManagementSettings 解析。
    /// </summary>
    private ManagementJoinRequest BuildManagementJoinRequest(DeviceInventory device)
    {
        var presetJson = ManagementPresetJson?.Trim();
        if (string.IsNullOrWhiteSpace(presetJson))
            throw new InvalidOperationException("请上传或粘贴 ClassIsland 集控配置文件（ManagementPreset.json）。");
        if (presetJson.Length > MaxManagementPresetJsonLength)
            throw new InvalidOperationException("集控配置文件不能超过 256 KB。");
        try
        {
            using var document = JsonDocument.Parse(presetJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("集控配置文件必须是 JSON 对象。");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("集控配置文件不是有效的 JSON。");
        }

        return new ManagementJoinRequest
        {
            PresetJson = presetJson,
            ClassIdentity = string.IsNullOrWhiteSpace(device.ClassName) ? null : device.ClassName.Trim(),
        };
    }

    private List<Guid> ParseGuidQuery(string name) => Request.Query[name]
        .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToList();

    protected async Task LoadBatchAsync(CancellationToken ct)
    {
        Classes = await Classrooms.ListAsync(ct);
        Groups = await Classrooms.ListGroupsAsync(ct);
        Devices = await DevicesService.ListAsync(ct);
        KnownPlugins = Devices
            .SelectMany(device => device.Plugins.Select(plugin => (device, plugin)))
            .Where(x => !string.IsNullOrWhiteSpace(x.plugin.Id))
            .GroupBy(x => x.plugin.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First().plugin;
                return new KnownPlugin(
                    first.Id,
                    string.IsNullOrWhiteSpace(first.Name) ? first.Id : first.Name,
                    first.Version,
                    first.IsEnabled,
                    group.Select(x => x.device.DeviceName).Distinct(StringComparer.CurrentCulture).ToList());
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private bool TargetsSelected() =>
        SelectedClassIds.Count > 0 || SelectedGroupIds.Count > 0 || SelectedConnectionIds.Count > 0;

    private IActionResult Back(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage();
    }

    protected static bool RequiresRiskConfirmation(BatchOperationKind operation) => operation is
        BatchOperationKind.Power or
        BatchOperationKind.DistributeProfile or
        BatchOperationKind.UninstallPlugins or
        BatchOperationKind.SetPluginManagementPolicy or
        BatchOperationKind.UpgradePlugins or
        BatchOperationKind.UpgradeClassIsland or
        BatchOperationKind.RestartClassIsland or
        BatchOperationKind.JoinManagement;

    protected static string OperationName(BatchOperationKind operation) => operation switch
    {
        BatchOperationKind.Notify => "通知广播",
        BatchOperationKind.ClearNotifications => "清除通知",
        BatchOperationKind.Power => "电源操作",
        BatchOperationKind.VoiceMessage => "语音消息",
        BatchOperationKind.UpdateTimeLayout => "时间表更新",
        BatchOperationKind.DistributeProfile => "档案分发",
        BatchOperationKind.InstallPlugins => "插件安装",
        BatchOperationKind.UninstallPlugins => "插件卸载",
        BatchOperationKind.SetPluginEnabled => "插件启停",
        BatchOperationKind.SetPluginManagementPolicy => "插件管理策略",
        BatchOperationKind.RefreshSoftwareInventory => "版本刷新",
        BatchOperationKind.UpgradePlugins => "插件升级",
        BatchOperationKind.UpgradeClassIsland => "ClassIsland 升级",
        BatchOperationKind.RestartClassIsland => "重启 ClassIsland",
        BatchOperationKind.JoinManagement => "加入集控",
        _ => "批量操作",
    };

    protected static BatchDeviceItemResult ToItemResult(DeviceCommandResult result) => new()
    {
        TargetId = result.CredentialId == Guid.Empty ? result.ClassId : result.CredentialId,
        ConnectionId = result.ConnectionId,
        ClassId = result.ClassId,
        TargetName = result.DeviceName,
        Success = result.Success,
        Message = result.Success ? null : result.Message,
    };

    protected static string Summarize(IReadOnlyList<BatchDeviceItemResult> results)
    {
        var ok = results.Count(x => x.Success);
        var failures = results.Where(x => !x.Success).ToList();
        return failures.Count == 0
            ? $"批量操作已完成（{ok} 台设备）。"
            : $"批量操作完成 {ok} 台，失败 {failures.Count} 台，详见下方逐设备结果。";
    }

    private void SaveResults(IReadOnlyList<BatchDeviceItemResult> results) =>
        TempData[ResultsKey] = JsonSerializer.Serialize(results, JsonDefaults.Options);

    private void RestoreResults()
    {
        if (TempData[ResultsKey] is not string json) return;
        try
        {
            LastResults = JsonSerializer.Deserialize<List<BatchDeviceItemResult>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            LastResults = [];
        }
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    public sealed record KnownPlugin(
        string Id,
        string Name,
        string Version,
        bool Enabled,
        IReadOnlyList<string> DeviceNames);
}
