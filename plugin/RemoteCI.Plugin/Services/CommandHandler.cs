using System.Globalization;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>全部远程写操作的唯一执行入口；只返回 ClassIsland 实际执行后的结果。</summary>
public sealed class CommandHandler
{
    private readonly ScheduleCatalog _schedules;
    private readonly IScheduleBackend _scheduleBackend;
    private readonly IProfileWriteOperations _profileOps;
    private readonly RemoteNotificationProvider _notifications;
    private readonly ClassIslandHostControlService _hostControl;
    private readonly ILogger _logger;
    private readonly ExtensionCommandRouter _extensionRouter;
    private readonly VoiceMessagePlayer _voiceMessages;

    public CommandHandler(
        ScheduleCatalog schedules,
        IScheduleBackend scheduleBackend,
        IProfileWriteOperations profileOps,
        ClassIslandHostControlService hostControl,
        IEnumerable<IHostedService> hostedServices,
        IRemoteCiExtensionRegistry extensions,
        ILoggerFactory loggerFactory,
        VoiceMessagePlayer? voiceMessages = null)
    {
        _schedules = schedules;
        _scheduleBackend = scheduleBackend;
        _profileOps = profileOps;
        _hostControl = hostControl;
        _notifications = hostedServices.OfType<RemoteNotificationProvider>().Single();
        _logger = loggerFactory.CreateLogger<CommandHandler>();
        _extensionRouter = new ExtensionCommandRouter(extensions, loggerFactory);
        _voiceMessages = voiceMessages ?? new VoiceMessagePlayer();
    }

    public event Action<ClassEvent>? NotificationSent;
    public event Action? ScheduleChanged;
    public event Action? HostStateChanged;

    /// <summary>插件停止时取消尚未执行的睡眠/休眠电源操作。</summary>
    public void CancelPendingPowerActions() => _hostControl.CancelPendingPowerActions();
    public void StopVoiceMessage() => _voiceMessages.Stop();

    public async Task<CommandResult> HandleAsync(CommandMessage command)
    {
        // 扩展命令同时使用独立扩展权限、服务端策略和注册项动态权限，不走静态命令表。
        if (command.Command == CommandKind.RunExtension)
            return await _extensionRouter.RunAsync(command);

        var required = CommandPermissions.Required(command.Command);
        if (required == UserPermissions.None)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"未知指令：{command.Command}");
        if (command.RequestedBy is null || !command.RequestedBy.Permissions.HasFlag(required))
            return CommandResult.Failure(CommandResultCodes.Forbidden, "权限不足");

        try
        {
            return command.Command switch
            {
                CommandKind.ChangeSchedule => await HandleScheduleChangeAsync(command.ScheduleChange),
                CommandKind.SendNotification => await HandleNotificationAsync(
                    command.Notification,
                    GetNotificationSenderName(command.RequestedBy)),
                CommandKind.SendVoiceMessage => await HandleVoiceMessageAsync(command.VoiceMessage,
                    GetNotificationSenderName(command.RequestedBy)),
                CommandKind.ClearNotifications => await HandleClearNotificationsAsync(),
                CommandKind.TeacherComing => await HandleTeacherComingAsync(),
                CommandKind.SetMainMenuVisibility => await HandleMainMenuVisibilityAsync(command.MainMenuVisible),
                CommandKind.Power => HandlePowerAction(command.PowerAction),
                CommandKind.Volume => HandleVolume(command.Volume),
                _ => CommandResult.Failure(CommandResultCodes.InvalidRequest, $"未知指令：{command.Command}"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoteCI 指令执行失败：{Command}", command.Command);
            return CommandResult.Failure(CommandResultCodes.InternalError, "指令执行异常，请查看 ClassIsland 日志");
        }
    }

    private async Task<CommandResult> HandleScheduleChangeAsync(ScheduleChangeRequest? request)
    {
        if (ValidateScheduleChangeRequest(request, DateTime.Today, out var date) is { } validationError)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, validationError);

        var result = await Dispatcher.UIThread.InvokeAsync(() => ApplyScheduleChange(date, request!));
        if (result.Success) ScheduleChanged?.Invoke();
        return result;
    }

    /// <summary>换课请求的形态校验（纯逻辑，先于 UI 线程执行）；通过返回 null 并给出解析后的日期。</summary>
    internal static string? ValidateScheduleChangeRequest(
        ScheduleChangeRequest? request, DateTime today, out DateTime date)
    {
        date = default;
        if (request is null || !TryParseDate(request.Date, out date))
            return "换课日期无效";
        if (date < today || date >= today.AddDays(7))
            return "只能修改今天起未来七天的课表";
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
            return "缺少课表修订号";
        return null;
    }

    private CommandResult ApplyScheduleChange(DateTime date, ScheduleChangeRequest request) =>
        ScheduleChangeExecutor.Apply(
            date, request, _schedules, _scheduleBackend, _profileOps,
            ex => _logger.LogError(ex, "保存 {Date} 临时课表失败", date));

    private async Task<CommandResult> HandleNotificationAsync(NotificationRequest? request, string senderName)
    {
        if (request is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少通知内容");
        var message = NormalizeNotificationMessage(request.Message);
        if (message.Length > 500)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "通知正文不能超过 500 个字符");
        // 服务端会按全局设置注入 ForceSenderInTitle；null 按旧行为视为开启。
        var title = BuildNotificationTitle(senderName, request.Title, request.ForceSenderInTitle != false);
        await _notifications.ShowRemoteNotificationAsync(
            title,
            message,
            request.IsNotificationEffectEnabled,
            request.IsNotificationSoundEnabled,
            request.IsSpeechEnabled);
        NotificationSent?.Invoke(new ClassEvent
        {
            Event = ClassEventKind.Custom,
            Subject = title,
            Message = message,
        });
        return Success("通知已在 ClassIsland 显示并广播到在线手表");
    }

    private async Task<CommandResult> HandleVoiceMessageAsync(VoiceMessageRequest? request, string senderName)
    {
        if (!VoiceMessageRequest.TryDecode(request, out var audio))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒");
        var title = $"来自{senderName}的语音消息";
        var result = await Dispatcher.UIThread.InvokeAsync(() => _voiceMessages.Play(audio, title));
        if (!result.Success) return result;
        try
        {
            // 语音自行播放，通知不叠加提示音或文字朗读。
            await _notifications.ShowVoiceMessageNotificationAsync(title);
        }
        catch { _voiceMessages.Stop(); throw; }
        NotificationSent?.Invoke(new ClassEvent { Event = ClassEventKind.Custom, Subject = title, Message = string.Empty });
        return result;
    }

    private async Task<CommandResult> HandleClearNotificationsAsync()
    {
        if (!_hostControl.IsNotificationPlaying) return Success("ClassIsland 当前没有提醒");
        await _hostControl.ClearNotificationsAsync();
        HostStateChanged?.Invoke();
        return Success("已清除 ClassIsland 提醒");
    }

    /// <summary>“老师来了”由插件原子执行显示、等待和清除，客户端只发送一次命令。</summary>
    private Task<CommandResult> HandleTeacherComingAsync() => RunTeacherComingWorkflowAsync(
        () => HandleNotificationAsync(new NotificationRequest
        {
            Title = "老师来了",
            Message = "老师来了",
            ForceSenderInTitle = false,
            IsNotificationEffectEnabled = true,
            IsNotificationSoundEnabled = false,
            IsSpeechEnabled = false,
        }, string.Empty),
        HandleClearNotificationsAsync);

    /// <summary>可测试的顺序编排：仅显示成功后等待 1 秒并清除，任一步失败都原样返回。</summary>
    internal static async Task<CommandResult> RunTeacherComingWorkflowAsync(
        Func<Task<CommandResult>> show,
        Func<Task<CommandResult>> clear,
        Func<TimeSpan, Task>? delay = null)
    {
        var shown = await show();
        if (!shown.Success) return shown;
        await (delay ?? Task.Delay)(TimeSpan.FromSeconds(1));
        var cleared = await clear();
        return cleared.Success
            ? Success("已显示“老师来了”并自动清除")
            : cleared;
    }

    private async Task<CommandResult> HandleMainMenuVisibilityAsync(bool? visible)
    {
        if (visible is null) return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少主界面显隐状态");
        await _hostControl.SetMainMenuVisibilityAsync(visible.Value);
        HostStateChanged?.Invoke();
        return Success(visible.Value ? "已显示 ClassIsland 主界面" : "已隐藏 ClassIsland 主界面");
    }

    private CommandResult HandlePowerAction(PowerActionKind? action)
    {
        if (action is null || !Enum.IsDefined(action.Value))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "电源操作无效");
        _hostControl.SchedulePowerAction(action.Value);
        return Success(action.Value switch
        {
            PowerActionKind.Shutdown => "Windows 即将关机",
            PowerActionKind.Restart => "Windows 即将重启",
            PowerActionKind.Sleep => "Windows 即将进入睡眠",
            PowerActionKind.Hibernate => "Windows 即将进入休眠",
            _ => "电源操作已提交",
        });
    }

    private CommandResult HandleVolume(VolumeControlRequest? request)
    {
        if (request is null || request is { Level: null, Muted: null })
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少音量控制参数");
        if (request.Level is < 0 or > 100)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "音量必须在 0-100 之间");

        _hostControl.SetVolume(request.Level, request.Muted);
        HostStateChanged?.Invoke();
        return Success(request.Muted switch
        {
            true => "电脑已静音",
            false => "电脑已取消静音",
            _ => $"电脑音量已调至 {request.Level}%",
        });
    }

    /// <summary>在最终执行端统一添加署名，是否强制由服务端全局设置决定。</summary>
    internal static string GetNotificationSenderName(UserProfile requestedBy) => requestedBy.DisplayName.Trim();

    internal static string BuildNotificationTitle(string senderName, string? requestedTitle, bool forceSenderInTitle = true)
    {
        var title = NormalizeNotificationTitle(requestedTitle);
        return forceSenderInTitle ? $"由{senderName.Trim()}发送：{title}" : title;
    }

    /// <summary>标题留空时统一使用默认标题，并负责 60 字截断与首尾空白清理。</summary>
    internal static string NormalizeNotificationTitle(string? requestedTitle)
    {
        var title = requestedTitle?.Trim();
        return string.IsNullOrWhiteSpace(title) ? "RemoteCI 通知" : title[..Math.Min(title.Length, 60)];
    }

    /// <summary>ClassIsland 支持只有标题的通知，因此空正文保持为空，仅清理首尾空白。</summary>
    internal static string NormalizeNotificationMessage(string? message) => message?.Trim() ?? string.Empty;

    private static bool TryParseDate(string value, out DateTime date) => DateTime.TryParseExact(
        value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}
