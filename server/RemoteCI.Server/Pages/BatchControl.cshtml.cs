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
// 文件分发经本页上传，请求体上限放宽到协议上限；FileTransferService 再按服务端配置的单文件上限复核。
[RequestSizeLimit(FileDistributionRequest.MaxTransferBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FileDistributionRequest.MaxTransferBytes + 1024 * 1024)]
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

    /// <summary>重复次数；null 或 &lt; 1 时按 1 次处理。</summary>
    [BindProperty]
    public int? BroadcastRepeatCounts { get; set; }

    /// <summary>正文横向滚动显示；默认关闭。</summary>
    [BindProperty]
    public bool BroadcastIsRollingEnabled { get; set; }

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

    /// <summary>安装或升级插件时先由服务端从插件市场下载插件包，再经文件中转分发到设备。</summary>
    [BindProperty]
    public bool ServerPreDownload { get; set; }

    /// <summary>安装插件时同时授权这些插件接入 RemoteCI 扩展，插件启动后设备端不再弹窗询问。</summary>
    [BindProperty]
    public bool GrantExtensionAccess { get; set; }

    /// <summary>扩展接入授权：保持授权（设备端不能解除）或解除保持。</summary>
    [BindProperty]
    public ExtensionAccessAction ExtensionAccessAction { get; set; } = ExtensionAccessAction.Lock;

    /// <summary>“从 CIPX 安装”已上传到服务端中转的插件包传输编号。</summary>
    [BindProperty]
    public List<string> PluginPackageTransferIds { get; set; } = [];

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
    public string? ProfileImportName { get; set; }

    [BindProperty]
    public bool ProfileReplaceCurrent { get; set; }

    [BindProperty]
    public bool ProfileEnableImported { get; set; } = true;

    [BindProperty]
    public bool ProfileReplaceExisting { get; set; }

    /// <summary>管理员上传的 ClassIsland 集控配置文件（ManagementPreset.json）内容。</summary>
    [BindProperty]
    public string? ManagementPresetJson { get; set; }

    [BindProperty]
    public bool Force { get; set; }

    /// <summary>批量执行扩展功能时的目标扩展 Id。</summary>
    [BindProperty]
    public string? BatchExtensionId { get; set; }

    /// <summary>批量执行扩展功能时提交的参数。</summary>
    [BindProperty]
    public List<ExtensionFieldInput> BatchExtensionInputs { get; set; } = [];

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

    /// <summary>
    /// 批量控制页的“扩展插件”区：汇总全部班级设备上报的扩展分组，只保留管理员已启用的扩展功能。
    /// 各班安装同一个支持 RemoteCI 的插件后，这里会自动出现该插件的功能与设置入口。
    /// </summary>
    public IReadOnlyList<ExtensionGroupView> BatchExtensionGroups { get; private set; } = [];
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
        if (MobileUi.IsMobile(HttpContext) && operation is not (BatchOperationKind.Notify or BatchOperationKind.ClearNotifications or BatchOperationKind.Power or BatchOperationKind.VoiceMessage)) return false;
        if (MobileUi.IsMobile(HttpContext) && this is ControlModel control)
            return operation switch { BatchOperationKind.Notify => control.CanSendNotifications, BatchOperationKind.ClearNotifications => control.CanClearNotifications,
                BatchOperationKind.Power => control.CanControlPower, BatchOperationKind.VoiceMessage => control.CanSendVoiceMessages, _ => false };
        if (!IsSingleControl) return true;
        if (CommandForOperation(operation) is not { } command) return false;
        var required = CommandPermissions.Required(command);
        return required != UserPermissions.None && ClassPermissions.HasFlag(required);
    }

    /// <summary>是否存在任一可见操作：无权限时连目标选择弹窗也不渲染，避免把设备清单泄露到页面。</summary>
    public bool HasAnyOperations => HasClassroomOperations || HasProfileOperations || HasSoftwareOperations ||
        HasMaintenanceOperations;

    /// <summary>分组可见性：某一组全部操作都无权限时整组隐藏，避免出现空面板。</summary>
    public bool HasClassroomOperations =>
        CanUseOperation(BatchOperationKind.Notify) ||
        CanUseOperation(BatchOperationKind.ClearNotifications) ||
        CanUseOperation(BatchOperationKind.Power) ||
        CanUseOperation(BatchOperationKind.VoiceMessage);

    public bool HasProfileOperations =>
        CanUseOperation(BatchOperationKind.UpdateTimeLayout) ||
        CanUseOperation(BatchOperationKind.DistributeProfile) ||
        CanUseOperation(BatchOperationKind.JoinManagement);

    public bool HasSoftwareOperations =>
        CanUseOperation(BatchOperationKind.InstallPlugins) ||
        CanUseOperation(BatchOperationKind.UninstallPlugins) ||
        CanUseOperation(BatchOperationKind.InstallPluginPackages) ||
        CanUseOperation(BatchOperationKind.SetPluginEnabled) ||
        CanUseOperation(BatchOperationKind.SetExtensionAccess) ||
        CanUseOperation(BatchOperationKind.RefreshSoftwareInventory) ||
        CanUseOperation(BatchOperationKind.UpgradePlugins) ||
        CanUseOperation(BatchOperationKind.UpgradeClassIsland) ||
        CanUseOperation(BatchOperationKind.RestartClassIsland);

    /// <summary>远程维护组：终端与文件分发只会影响设备文件系统，单班页还要求系统管理员。</summary>
    public bool HasMaintenanceOperations =>
        CanUseMaintenanceOperation(BatchOperationKind.ExecuteTerminalCommand) ||
        CanUseMaintenanceOperation(BatchOperationKind.SendFile);

    /// <summary>终端与文件分发的可见性：批量页整体已限定管理员；单班页要求系统管理员加班级维护权限。</summary>
    public bool CanUseMaintenanceOperation(BatchOperationKind operation) =>
        !MobileUi.IsMobile(HttpContext) && (IsSingleControl
            ? IsAdmin && CommandForOperation(operation) is { } command &&
              CommandPermissions.Required(command) is var required &&
              required != UserPermissions.None &&
              ClassPermissions.HasFlag(required)
            : CanUseOperation(operation));

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
        BatchOperationKind.InstallPluginPackages => CommandKind.InstallPluginPackages,
        BatchOperationKind.SetPluginEnabled => CommandKind.SetPluginEnabled,
        BatchOperationKind.SetExtensionAccess => CommandKind.SetExtensionAccess,
        BatchOperationKind.RefreshSoftwareInventory => CommandKind.RefreshSoftwareInventory,
        BatchOperationKind.UpgradePlugins => CommandKind.UpgradePlugins,
        BatchOperationKind.UpgradeClassIsland => CommandKind.UpgradeClassIsland,
        BatchOperationKind.RestartClassIsland => CommandKind.RestartClassIsland,
        BatchOperationKind.JoinManagement => CommandKind.JoinManagement,
        BatchOperationKind.ExecuteTerminalCommand => CommandKind.ExecuteTerminalCommand,
        BatchOperationKind.SendFile => CommandKind.SendFile,
        BatchOperationKind.RunExtension => CommandKind.RunExtension,
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
            SaveResults(results);
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
                VoiceMessage = CreateVoiceRequest(audio.ToArray()),
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
                VoiceMessage = CreateVoiceRequest(audio.ToArray()),
                RequestedBy = profile,
            },
            "语音消息",
            BatchCommandTimeout,
            ct)).Select(ToItemResult)).ToList();
        SaveResults(results);
        var ok = results.Count(x => x.Success);
        return new JsonResult(new { success = ok > 0, message = $"语音消息已发送到当前班级 {ok} 台设备。" });
    }

    // ---------- 远程终端与文件分发（AJAX：回执包含逐设备输出或保存路径） ----------

    /// <summary>批量终端执行：命令文本经表单体提交，目标复用共享弹窗的查询参数。</summary>
    public async Task<IActionResult> OnPostExecuteTerminalAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        return await ExecuteTerminalCoreAsync(
            ParseGuidQuery("classIds"), ParseGuidQuery("groupIds"), ParseGuidQuery("connectionIds"), ct);
    }

    /// <summary>单班终端执行：目标固定为当前班级，且要求系统管理员加班级维护权限。</summary>
    public async Task<IActionResult> OnPostSingleTerminalAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!IsSingleControl || !CanRunMaintenanceInCurrentClass()) return RemoteOpDenied();
        return await ExecuteTerminalCoreAsync([CurrentClassId], [], [], ct);
    }

    /// <summary>批量文件分发：multipart 上传单个文件，目标复用共享弹窗的查询参数。</summary>
    public async Task<IActionResult> OnPostDistributeFileAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        return await DistributeFileCoreAsync(
            ParseGuidQuery("classIds"), ParseGuidQuery("groupIds"), ParseGuidQuery("connectionIds"), ct);
    }

    /// <summary>单班文件分发：目标固定为当前班级，且要求系统管理员加班级维护权限。</summary>
    public async Task<IActionResult> OnPostSingleDistributeFileAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!IsSingleControl || !CanRunMaintenanceInCurrentClass()) return RemoteOpDenied();
        return await DistributeFileCoreAsync([CurrentClassId], [], [], ct);
    }

    /// <summary>单班页执行远程维护的前置复核，与卡片可见性使用同一口径。</summary>
    private bool CanRunMaintenanceInCurrentClass() =>
        IsAdmin && ClassPermissions.HasFlag(UserPermissions.ManageUsers);

    private IActionResult RemoteOpDenied() => new JsonResult(CommandResult.Failure(
        CommandResultCodes.Forbidden, "没有远程维护权限"));

    private async Task<IActionResult> ExecuteTerminalCoreAsync(
        IReadOnlyCollection<Guid> classIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken ct)
    {
        var commandText = Request.Form["command"].ToString().Trim();
        if (commandText.Length == 0)
            return RemoteOpJson(false, "请输入要执行的命令。", []);
        if (commandText.Length > TerminalCommandRequest.MaxCommandLength)
            return RemoteOpJson(false, $"命令不能超过 {TerminalCommandRequest.MaxCommandLength} 个字符。", []);
        if (!int.TryParse(Request.Form["timeoutSeconds"], out var timeoutSeconds) ||
            timeoutSeconds is < 1 or > TerminalCommandRequest.MaxTimeoutSeconds)
            timeoutSeconds = TerminalCommandRequest.DefaultTimeoutSeconds;
        var workingDirectory = Request.Form["workingDirectory"].ToString().Trim();
        if (workingDirectory.Length > TerminalCommandRequest.MaxWorkingDirectoryLength)
            return RemoteOpJson(false, "工作目录路径过长。", []);

        var plan = await DevicesService.ResolveAsync(classIds, groupIds, connectionIds, ct);
        if (plan.Targets.Count == 0 && plan.Failures.Count == 0)
            return RemoteOpJson(false, "没有找到可用的在线设备。", []);
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        var dispatch = await DevicesService.DispatchAsync(
            plan.Targets,
            _ => new CommandMessage
            {
                Command = CommandKind.ExecuteTerminalCommand,
                TerminalCommand = new TerminalCommandRequest
                {
                    Command = commandText,
                    TimeoutSeconds = timeoutSeconds,
                    WorkingDirectory = workingDirectory.Length > 0 ? workingDirectory : null,
                },
                RequestedBy = profile,
            },
            "远程终端",
            BatchCommandTimeout,
            ct,
            result => result.Data ?? result.Message);
        return RemoteOpResultsJson(
            plan.Failures.Select(ToItemResult).Concat(dispatch.Select(ToItemResult)).ToList());
    }

    private async Task<IActionResult> DistributeFileCoreAsync(
        IReadOnlyCollection<Guid> classIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken ct)
    {
        var transfers = HttpContext.RequestServices.GetRequiredService<FileTransferService>();
        var file = Request.Form.Files["file"];
        if (file is null || file.Length == 0)
            return RemoteOpJson(false, "请选择要分发的文件。", []);
        if (file.Length > transfers.MaxFileBytes)
            return RemoteOpJson(false, $"文件不能超过 {FileTransferService.FormatSize(transfers.MaxFileBytes)}。", []);
        if (FileDistributionRequest.SanitizeFileName(file.FileName) is null)
            return RemoteOpJson(false, "文件名无效。", []);
        if (!Enum.TryParse(Request.Form["targetFolder"].ToString(), out FileTargetFolder targetFolder) ||
            !Enum.IsDefined(targetFolder))
            return RemoteOpJson(false, "目标文件夹无效。", []);
        var overwrite = string.Equals(Request.Form["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return RemoteOpJson(false, "会话已失效，请刷新后重试。", []);
        FileTransferRecord record;
        try
        {
            await using var stream = file.OpenReadStream();
            record = await transfers.CreateAsync(stream, file.FileName, profile, ct);
        }
        catch (FileTransferException ex)
        {
            return RemoteOpJson(false, ex.Message, []);
        }
        // 命令回执只代表设备已开始接收；页面随后轮询 FileTransferStatus 显示逐台下载进度。
        var view = await HttpContext.RequestServices.GetRequiredService<FileTransferDispatcher>().SendAsync(
            record, classIds, groupIds, connectionIds, targetFolder, overwrite, _ => profile, ct);
        return FileTransferJson(view);
    }

    /// <summary>文件分发进度：上传者本人或系统管理员可查询。</summary>
    public async Task<IActionResult> OnGetFileTransferStatusAsync(string id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        var transfers = HttpContext.RequestServices.GetRequiredService<FileTransferService>();
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        return profile is not null && transfers.Get(id) is { } record && FileTransferService.CanManage(record, profile)
            ? FileTransferJson(transfers.ToView(record))
            : RemoteOpJson(false, "文件不存在或已过期。", []);
    }

    private static IActionResult FileTransferJson(FileTransferView view)
    {
        var done = view.Deliveries.Count(x => x.State == FileTransferState.Completed);
        var failed = view.Deliveries.Count(x => x.State == FileTransferState.Failed);
        var finished = done + failed == view.Deliveries.Count;
        return new JsonResult(new
        {
            success = done > 0 || !finished,
            finished,
            message = view.Deliveries.Count == 0
                ? "没有可执行的设备。"
                : finished
                    ? failed == 0 ? $"已送达 {done} 台设备。" : $"送达 {done} 台，失败 {failed} 台。"
                    : $"正在接收：已完成 {done}/{view.Deliveries.Count} 台。",
            transfer = view,
            results = view.Deliveries.Select(x => new
            {
                name = x.TargetName,
                success = x.State != FileTransferState.Failed,
                state = x.State.ToString(),
                percent = x.Percent,
                message = x.State switch
                {
                    FileTransferState.Completed => x.SavedPath ?? x.Message ?? "已保存",
                    FileTransferState.Failed => x.Message ?? "失败",
                    FileTransferState.Downloading => $"{FileTransferService.FormatSize(x.ReceivedBytes)} / {FileTransferService.FormatSize(x.TotalBytes)}" +
                        (string.IsNullOrEmpty(x.Message) || x.Message.StartsWith("开始接收", StringComparison.Ordinal) ? string.Empty : $"（{x.Message}）"),
                    _ => "等待设备开始接收",
                },
            }),
        });
    }

    private static IActionResult RemoteOpJson(bool success, string message, IReadOnlyList<object> results) =>
        new JsonResult(new { success, message, results });

    /// <summary>统一回执形状：前端按 name/success/message 逐设备渲染，message 可能是输出、保存路径或失败原因。</summary>
    private static IActionResult RemoteOpResultsJson(IReadOnlyList<BatchDeviceItemResult> results)
    {
        var ok = results.Count(x => x.Success);
        var message = results.Count == 0
            ? "没有可执行的设备。"
            : ok == results.Count
                ? $"操作已完成（{ok} 台设备）。"
                : $"操作完成 {ok} 台，失败 {results.Count - ok} 台。";
        return new JsonResult(new
        {
            success = ok > 0,
            message,
            results = results.Select(x => new
            {
                name = x.TargetName,
                success = x.Success,
                message = x.Message ?? string.Empty,
            }),
        });
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
                    IsRollingEnabled = BroadcastIsRollingEnabled,
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
            BatchOperationKind.InstallPlugins when ServerPreDownload =>
                await BuildMarketInstallFactoryAsync(profile, ct),
            BatchOperationKind.InstallPlugins => _ => new CommandMessage
            {
                Command = CommandKind.InstallPlugins,
                PluginManagement = BuildPluginRequest(PluginActionKind.Install),
                RequestedBy = profile,
            },
            BatchOperationKind.InstallPluginPackages => BuildUploadedPackagesFactory(profile),
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
            BatchOperationKind.SetExtensionAccess => _ => new CommandMessage
            {
                Command = CommandKind.SetExtensionAccess,
                ExtensionAccess = BuildExtensionAccessRequest(),
                RequestedBy = profile,
            },
            BatchOperationKind.RefreshSoftwareInventory => _ => new CommandMessage
            {
                Command = CommandKind.RefreshSoftwareInventory,
                RequestedBy = profile,
            },
            BatchOperationKind.UpgradePlugins when ServerPreDownload =>
                await BuildMarketUpgradeFactoryAsync(profile, ct),
            BatchOperationKind.UpgradePlugins => _ => new CommandMessage
            {
                Command = CommandKind.UpgradePlugins,
                // 未勾选具体插件时沿用旧语义：升级设备上全部可更新插件。
                SoftwareUpgrade = new SoftwareUpgradeRequest
                {
                    Force = Force,
                    PluginIds = SelectedPluginIds() is { Count: > 0 } ids ? ids : null,
                },
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
            BatchOperationKind.RunExtension => BuildExtensionCommandFactory(profile),
            // 终端与文件分发走 AJAX 处理器（需要逐设备带回输出或保存路径），不走标准表单投递。
            BatchOperationKind.ExecuteTerminalCommand or BatchOperationKind.SendFile =>
                throw new InvalidOperationException("该操作请通过远程维护面板执行。"),
            _ => throw new InvalidOperationException("不支持的批量操作。"),
        };
    }

    /// <summary>
    /// 批量执行扩展：参数按全部班级汇总的扩展声明补齐默认值并预校验；
    /// 每台设备的插件仍会按自身注册的声明复核，未安装该扩展的设备返回“扩展功能不存在”。
    /// </summary>
    private Func<DeviceInventory, CommandMessage> BuildExtensionCommandFactory(UserProfile? profile)
    {
        var definition = BatchExtensionGroups
            .SelectMany(group => group.Extensions)
            .FirstOrDefault(extension => string.Equals(extension.Id, BatchExtensionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("扩展功能不存在、已停用或尚未同步。");
        var submitted = ExtensionFieldInput.ToValues(BatchExtensionInputs);
        var args = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters ?? [])
        {
            submitted.TryGetValue(parameter.Key, out var value);
            args[parameter.Key] = value ?? parameter.DefaultValue;
        }
        args = ExtensionFieldValidator.ValidateArguments(definition.Parameters ?? [], args, out var error);
        if (error is not null) throw new InvalidOperationException(error);
        return _ => new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = definition.Id,
            ExtensionArgs = new Dictionary<string, string?>(args, StringComparer.Ordinal),
            RequestedBy = profile,
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

    /// <summary>勾选的插件与手动填写的插件 Id 合并去重。</summary>
    private List<string> SelectedPluginIds() => PluginIds
        .Concat((PluginIdsText ?? string.Empty)
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>服务端预下载安装：按插件市场解析必选依赖，下载后经文件中转分发。</summary>
    private async Task<Func<DeviceInventory, CommandMessage>> BuildMarketInstallFactoryAsync(
        UserProfile? profile, CancellationToken ct)
    {
        var ids = SelectedPluginIds();
        if (ids.Count == 0)
            throw new InvalidOperationException("请至少选择一个插件，或填写插件 Id。");
        if (ids.Contains(RemoteCiPluginId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能远程安装 RemoteCI 自身；更新 RemoteCI 请使用“升级插件”或“从 CIPX 安装”。");
        var prepared = await PrepareMarketPackagesAsync(profile, async catalog =>
            PluginMarketCatalog.ResolveInstallSet(await catalog.GetAsync(ct: ct), ids), ct);
        return PackageFactory(prepared, PluginPackageInstallMode.Install, force: false, profile, GrantExtensionAccess);
    }

    /// <summary>服务端预下载升级：未勾选时取目标范围内设备上报的全部可更新插件。</summary>
    private async Task<Func<DeviceInventory, CommandMessage>> BuildMarketUpgradeFactoryAsync(
        UserProfile? profile, CancellationToken ct)
    {
        var ids = SelectedPluginIds();
        if (ids.Count == 0)
        {
            if (KnownPlugins.Count == 0) await LoadBatchAsync(ct);
            ids = KnownPlugins.Where(x => x.UpdateAvailable).Select(x => x.Id).ToList();
        }
        if (ids.Count == 0)
            throw new InvalidOperationException("设备上报的插件均已是最新版本；如需强制更新，请勾选要更新的插件。");
        var prepared = await PrepareMarketPackagesAsync(profile, async catalog =>
        {
            var index = await catalog.GetAsync(ct: ct);
            var found = ids.Select(index.Find).OfType<MarketPlugin>().ToList();
            if (found.Count == 0) throw new PluginMarketException("所选插件均不在 ClassIsland 插件市场中。");
            return found.Select(plugin => (plugin, false)).ToList();
        }, ct);
        return PackageFactory(prepared, PluginPackageInstallMode.Upgrade, Force, profile);
    }

    /// <summary>从 CIPX 安装：使用本页先行上传到中转的插件包，已安装的同名插件被覆盖。</summary>
    private Func<DeviceInventory, CommandMessage> BuildUploadedPackagesFactory(UserProfile? profile)
    {
        if (profile is null) throw new InvalidOperationException("会话已失效，请刷新后重试。");
        var packages = HttpContext.RequestServices.GetRequiredService<PluginPackageService>();
        var prepared = PluginPackageTransferIds
            .Distinct(StringComparer.Ordinal)
            .Select(id => packages.FindUploaded(id, profile))
            .OfType<PreparedPluginPackage>()
            .GroupBy(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();
        if (prepared.Count == 0)
            throw new InvalidOperationException("请先上传 .cipx 插件包（上传后在保留期内有效）。");
        if (prepared.Count > PluginPackageInstallRequest.MaxPackages)
            throw new InvalidOperationException($"一次最多安装 {PluginPackageInstallRequest.MaxPackages} 个插件包。");
        return PackageFactory(prepared, PluginPackageInstallMode.Install, force: false, profile, GrantExtensionAccess);
    }

    private async Task<IReadOnlyList<PreparedPluginPackage>> PrepareMarketPackagesAsync(
        UserProfile? profile,
        Func<PluginMarketCatalog, Task<IReadOnlyList<(MarketPlugin Plugin, bool IsDependency)>>> resolve,
        CancellationToken ct)
    {
        if (profile is null) throw new InvalidOperationException("会话已失效，请刷新后重试。");
        var services = HttpContext.RequestServices;
        try
        {
            var plugins = await resolve(services.GetRequiredService<PluginMarketCatalog>());
            if (plugins.Count > PluginPackageInstallRequest.MaxPackages)
                throw new InvalidOperationException($"一次最多安装 {PluginPackageInstallRequest.MaxPackages} 个插件包（含依赖）。");
            return await services.GetRequiredService<PluginPackageService>().PrepareFromMarketAsync(plugins, profile, ct);
        }
        catch (Exception ex) when (ex is PluginMarketException or FileTransferException)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    /// <summary>每台目标设备单独登记下载权限；不支持插件包安装的旧插件由能力检查逐台拒绝。</summary>
    private Func<DeviceInventory, CommandMessage> PackageFactory(
        IReadOnlyList<PreparedPluginPackage> prepared, PluginPackageInstallMode mode, bool force, UserProfile? profile,
        bool grantExtensionAccess = false)
    {
        var packages = HttpContext.RequestServices.GetRequiredService<PluginPackageService>();
        return device => new CommandMessage
        {
            Command = CommandKind.InstallPluginPackages,
            PluginPackages = new PluginPackageInstallRequest
            {
                Mode = mode,
                Force = force,
                RestartAfter = true,
                GrantExtensionAccess = grantExtensionAccess,
                Packages = prepared.Select(package => packages.CreateRef(package, device)).ToList(),
            },
            RequestedBy = profile,
        };
    }

    private const string RemoteCiPluginId = "remoteci.plugin";

    /// <summary>插件市场列表（AJAX）：供安装弹窗展示名称、Id、下载量与星标。</summary>
    public async Task<IActionResult> OnGetPluginMarketAsync(bool refresh, CancellationToken ct)
    {
        if (await RequirePluginManagerAsync() is { } denied) return denied;
        try
        {
            var index = await HttpContext.RequestServices.GetRequiredService<PluginMarketCatalog>().GetAsync(refresh, ct);
            return new JsonResult(new
            {
                success = true,
                fetchedAt = index.FetchedAt,
                plugins = index.Plugins.Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    description = x.Description,
                    author = x.Author,
                    version = x.Version,
                    downloadCount = x.DownloadCount,
                    starsCount = x.StarsCount,
                    url = x.Url,
                    dependencies = x.Dependencies.Where(d => d.IsRequired).Select(d => d.Id),
                }),
            });
        }
        catch (PluginMarketException ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    /// <summary>上传 .cipx（AJAX）：落入文件中转并解析清单，返回插件名称、Id 与版本。</summary>
    public async Task<IActionResult> OnPostUploadPluginPackageAsync(CancellationToken ct)
    {
        if (await RequirePluginManagerAsync(BatchOperationKind.InstallPluginPackages) is { } denied) return denied;
        var file = Request.Form.Files["file"];
        if (file is null || file.Length == 0)
            return new JsonResult(new { success = false, message = "请选择 .cipx 插件包。" });
        if (file.Length > PluginPackageService.MaxPackageBytes)
            return new JsonResult(new { success = false, message = "插件包不能超过 512 MB。" });
        var profile = await Identities.GetProfileAsync(CurrentUser.Id, ct);
        if (profile is null) return new JsonResult(new { success = false, message = "会话已失效，请刷新后重试。" });
        try
        {
            await using var stream = file.OpenReadStream();
            var package = await HttpContext.RequestServices.GetRequiredService<PluginPackageService>()
                .UploadAsync(stream, file.FileName, profile, ct);
            return new JsonResult(new
            {
                success = true,
                package = new
                {
                    transferId = package.Record.Id,
                    pluginId = package.Manifest.Id,
                    name = package.Manifest.Name,
                    version = package.Manifest.Version,
                    size = FileTransferService.FormatSize(package.Record.Size),
                },
            });
        }
        catch (FileTransferException ex)
        {
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    /// <summary>批量页要求系统管理员；单班页要求当前班级可以使用对应的插件操作。</summary>
    private async Task<IActionResult?> RequirePluginManagerAsync(
        BatchOperationKind operation = BatchOperationKind.InstallPlugins)
    {
        if (!IsSingleControl) return await RequireAdminAsync();
        if (await RequireAsync() is { } denied) return denied;
        return CanUseOperation(operation)
            ? null
            : new JsonResult(new { success = false, message = "没有插件管理权限" });
    }

    private PluginManagementRequest BuildPluginRequest(PluginActionKind action)
    {
        var ids = SelectedPluginIds();
        if (ids.Count == 0)
            throw new InvalidOperationException("请至少选择一个插件，或填写插件 Id。");
        if (action is not PluginActionKind.Install and not PluginActionKind.Uninstall and not PluginActionKind.Enable and not PluginActionKind.Disable)
            throw new InvalidOperationException("未知插件操作。");
        return new PluginManagementRequest
        {
            Action = action,
            PluginIds = ids,
            RestartAfter = true,
            GrantExtensionAccess = action == PluginActionKind.Install && GrantExtensionAccess,
        };
    }

    private ExtensionAccessRequest BuildExtensionAccessRequest()
    {
        if (!Enum.IsDefined(ExtensionAccessAction))
            throw new InvalidOperationException("未知扩展接入授权操作。");
        var ids = SelectedPluginIds()
            .Where(id => !string.Equals(id, RemoteCiPluginId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("请至少选择一个插件，或填写插件 Id。");
        if (ids.Count > ExtensionAccessRequest.MaxPlugins)
            throw new InvalidOperationException($"一次最多设置 {ExtensionAccessRequest.MaxPlugins} 个插件。");
        return new ExtensionAccessRequest { Action = ExtensionAccessAction, PluginIds = ids };
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
            ImportProfileName = ProfileImportName,
            ReplaceCurrentProfile = ProfileReplaceCurrent,
            EnableImportedProfile = ProfileEnableImported,
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

    /// <summary>语音正文是原始 PCM，“强制播放完成”与广播目标一样由 voice-message.js 写入查询参数。</summary>
    protected VoiceMessageRequest CreateVoiceRequest(byte[] audio) => new()
    {
        AudioBase64 = Convert.ToBase64String(audio),
        ForceComplete = string.Equals(Request.Query["forceComplete"], "true", StringComparison.OrdinalIgnoreCase),
    };

    protected async Task LoadBatchAsync(CancellationToken ct)
    {
        Classes = await Classrooms.ListAsync(ct);
        Groups = await Classrooms.ListGroupsAsync(ct);
        Devices = await DevicesService.ListAsync(ct);
        // 单班页只汇总当前班级设备上报的插件，避免看到其他班级的设备信息。
        var pluginSources = IsSingleControl ? Devices.Where(device => device.ClassId == CurrentClassId) : Devices;
        KnownPlugins = pluginSources
            .SelectMany(device => device.Plugins.Select(plugin => (device, plugin)))
            .Where(x => !string.IsNullOrWhiteSpace(x.plugin.Id))
            .GroupBy(x => x.plugin.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First().plugin;
                var outdated = group.Where(x => x.plugin.IsUpdateAvailable).ToList();
                return new KnownPlugin(
                    first.Id,
                    string.IsNullOrWhiteSpace(first.Name) ? first.Id : first.Name,
                    first.Version,
                    first.IsEnabled,
                    group.Select(x => x.device.DeviceName).Distinct(StringComparer.CurrentCulture).ToList(),
                    outdated.Select(x => x.plugin.LatestVersion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    outdated.Select(x => x.device.DeviceName).Distinct(StringComparer.CurrentCulture).ToList());
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
        if (!IsSingleControl) await LoadBatchExtensionGroupsAsync(ct);
    }

    private async Task LoadBatchExtensionGroupsAsync(CancellationToken ct)
    {
        var services = HttpContext.RequestServices;
        var groups = await services.GetRequiredService<ExtensionGroupService>().BuildForAllClassesAsync(ct: ct);
        var definitions = groups.SelectMany(group => group.Extensions).ToList();
        // 批量页只对系统管理员开放：按管理员身份取策略，只保留已启用的扩展。
        var enabled = (await services.GetRequiredService<ExtensionPolicyService>().ListForUserAsync(
                CurrentUser.Id, CurrentUser.Role, UserPermissions.All, definitions, ct))
            .Where(item => item.Enabled)
            .Select(item => item.Definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        BatchExtensionGroups = groups
            .Select(group => group with
            {
                Extensions = group.Extensions.Where(extension => enabled.Contains(extension.Id)).ToList(),
            })
            .Where(group => group.Extensions.Count > 0 || group.HasSettings)
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
        BatchOperationKind.InstallPluginPackages or
        BatchOperationKind.UpgradePlugins or
        BatchOperationKind.UpgradeClassIsland or
        BatchOperationKind.RestartClassIsland or
        BatchOperationKind.JoinManagement or
        BatchOperationKind.ExecuteTerminalCommand or
        BatchOperationKind.SendFile;

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
        BatchOperationKind.InstallPluginPackages => "插件包安装",
        BatchOperationKind.SetPluginEnabled => "插件启停",
        BatchOperationKind.SetExtensionAccess => "扩展接入授权",
        BatchOperationKind.RefreshSoftwareInventory => "版本刷新",
        BatchOperationKind.UpgradePlugins => "插件升级",
        BatchOperationKind.UpgradeClassIsland => "ClassIsland 升级",
        BatchOperationKind.RestartClassIsland => "重启 ClassIsland",
        BatchOperationKind.JoinManagement => "加入集控",
        BatchOperationKind.ExecuteTerminalCommand => "远程终端",
        BatchOperationKind.SendFile => "文件分发",
        BatchOperationKind.RunExtension => "扩展功能",
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

    protected void RestoreResults()
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

    /// <summary>按结果中的班级 Id 解析当前页面可见的班级名称，供批量与单班结果共用。</summary>
    public string ResultClassName(BatchDeviceItemResult result) =>
        Classes.FirstOrDefault(x => x.Id == result.ClassId)?.Name
        ?? (CurrentClass?.Id == result.ClassId ? CurrentClass.Name : "未知班级");

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
        IReadOnlyList<string> DeviceNames,
        string? LatestVersion,
        IReadOnlyList<string> OutdatedDeviceNames)
    {
        public bool UpdateAvailable => OutdatedDeviceNames.Count > 0;
    }
}
