using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using NotificationSettings = ClassIsland.Shared.Models.Notification.NotificationSettings;

namespace RemoteCI.Plugin.Services;

[NotificationProviderInfo(
    "d680fd32-26f0-43ef-9e40-ef75252d1bd4",
    "RemoteCI",
    "\ue0ff",
    "显示管理员从 RemoteCI 手表或 WebUI 发送的通知")]
public sealed class RemoteNotificationProvider : NotificationProviderBase<NotificationSettings>
{
    public async Task ShowVoiceMessageNotificationAsync(string title) =>
        await Dispatcher.UIThread.InvokeAsync(() => ShowNotification(BuildVoiceMessageNotification(Settings, title)));

    internal static NotificationRequest BuildVoiceMessageNotification(NotificationSettings settings, string title) =>
        BuildNotificationRequest(settings, title, string.Empty, new RemoteNotificationOptions(IsNotificationEffectEnabled: true));

    public async Task ShowRemoteNotificationAsync(string title, string message, RemoteNotificationOptions options)
    {
        var requests = BuildNotificationRequests(Settings, title, message, options);
        // ClassIsland 按调用顺序排队显示，多条请求即依次重复提醒。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var request in requests)
                ShowNotification(request);
        });
    }

    /// <summary>
    /// 滚动时重复次数由滚动模板消化，只需一条请求；静态正文无法“重复滚动”，
    /// 改为把整条提醒排队显示 N 次。每次都必须是新的请求实例，ClassIsland 会改写请求状态。
    /// </summary>
    internal static IReadOnlyList<NotificationRequest> BuildNotificationRequests(
        NotificationSettings providerSettings,
        string title,
        string message,
        RemoteNotificationOptions options)
    {
        var count = options.IsRollingEnabled ? 1 : options.EffectiveRepeatCounts;
        return Enumerable.Range(0, count)
            .Select(_ => BuildNotificationRequest(providerSettings, title, message, options))
            .ToList();
    }

    internal static NotificationRequest BuildNotificationRequest(
        NotificationSettings providerSettings,
        string title,
        string message,
        RemoteNotificationOptions options)
    {
        // ClassIsland 的提供方设置优先于请求设置；RemoteCI 的选项来自每条消息，因此禁用前者。
        providerSettings.IsSettingsEnabled = false;
        var duration = options.EffectiveDurationSeconds;
        var repeats = options.EffectiveRepeatCounts;
        var isSpeechEnabled = options.IsSpeechEnabled;
        return new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(title, hasRightIcon: false, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(4);
                x.IsSpeechEnabled = isSpeechEnabled;
            }),
            // ClassIsland 以 null 跳过正文阶段；空文本内容仍会显示一个空白正文区域。
            // 滚动模式借用 ClassIsland 集控的做法：正文用滚动文本模板重复滚动 N 遍，总时长 = 单条时长 × 次数。
            // 静态模式每条只显示一个持续时间，重复由 BuildNotificationRequests 排队多条完成。
            OverlayContent = string.IsNullOrWhiteSpace(message)
                ? null
                : options.IsRollingEnabled
                    ? NotificationContent.CreateRollingTextContent(
                        message,
                        TimeSpan.FromSeconds(duration) * repeats,
                        repeats,
                        factory: x => x.IsSpeechEnabled = isSpeechEnabled)
                    : NotificationContent.CreateSimpleTextContent(message, factory: x =>
                    {
                        x.Duration = TimeSpan.FromSeconds(duration);
                        x.IsSpeechEnabled = isSpeechEnabled;
                    }),
            // 每条远程提醒使用发送端选择的效果，不改动插件或 ClassIsland 的全局默认值。
            RequestNotificationSettings =
            {
                IsSettingsEnabled = true,
                IsNotificationEffectEnabled = options.IsNotificationEffectEnabled,
                IsNotificationSoundEnabled = options.IsNotificationSoundEnabled,
                IsSpeechEnabled = isSpeechEnabled,
                IsNotificationTopmostEnabled = options.IsNotificationTopmostEnabled,
            },
        };
    }
}

/// <summary>
/// 单条远程提醒的显示选项，由协议层 <see cref="RemoteCI.Shared.Models.NotificationRequest"/> 归一化得到，
/// 避免效果、声音、语音、置顶、时长、重复次数和滚动在各层逐字段传递。
/// 时长与重复次数在执行端再次限幅，旧服务端或局域网客户端发来的越界值也不会造成大量通知排队。
/// </summary>
public sealed record RemoteNotificationOptions(
    bool IsNotificationEffectEnabled = false,
    bool IsNotificationSoundEnabled = false,
    bool IsSpeechEnabled = false,
    bool IsNotificationTopmostEnabled = false,
    int DurationSeconds = RemoteCI.Shared.Models.NotificationRequest.DefaultDurationSeconds,
    int RepeatCounts = 1,
    bool IsRollingEnabled = true)
{
    public int EffectiveDurationSeconds => DurationSeconds <= 0
        ? RemoteCI.Shared.Models.NotificationRequest.DefaultDurationSeconds
        : Math.Min(DurationSeconds, RemoteCI.Shared.Models.NotificationRequest.MaxDurationSeconds);

    public int EffectiveRepeatCounts => Math.Clamp(RepeatCounts, 1, RemoteCI.Shared.Models.NotificationRequest.MaxRepeatCounts);

    /// <summary>省略 <c>isRollingEnabled</c> 的旧客户端保持升级前的滚动正文。</summary>
    public static RemoteNotificationOptions From(RemoteCI.Shared.Models.NotificationRequest request) => new(
        request.IsNotificationEffectEnabled,
        request.IsNotificationSoundEnabled,
        request.IsSpeechEnabled,
        request.IsNotificationTopmostEnabled,
        request.EffectiveDurationSeconds,
        request.EffectiveRepeatCounts,
        request.EffectiveRollingEnabled);
}
