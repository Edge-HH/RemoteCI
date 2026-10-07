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
        BuildNotificationRequest(settings, title, string.Empty, true, false, false);

    public async Task ShowRemoteNotificationAsync(
        string title,
        string message,
        bool isNotificationEffectEnabled,
        bool isNotificationSoundEnabled,
        bool isSpeechEnabled,
        bool isNotificationTopmostEnabled = false,
        int durationSeconds = 0,
        int repeatCounts = 1,
        bool isRollingEnabled = false)
    {
        var requests = BuildNotificationRequests(
            Settings,
            title,
            message,
            isNotificationEffectEnabled,
            isNotificationSoundEnabled,
            isSpeechEnabled,
            isNotificationTopmostEnabled,
            durationSeconds,
            repeatCounts,
            isRollingEnabled);
        // ClassIsland 按调用顺序排队显示，多条请求即依次重复提醒。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var request in requests)
                ShowNotification(request);
        });
    }

    /// <summary>
    /// 开启滚动时重复次数由滚动模板消化，只需一条请求；关闭滚动时静态正文无法“重复滚动”，
    /// 改为把整条提醒排队显示 N 次。每次都必须是新的请求实例，ClassIsland 会改写请求状态。
    /// </summary>
    internal static IReadOnlyList<NotificationRequest> BuildNotificationRequests(
        NotificationSettings providerSettings,
        string title,
        string message,
        bool isNotificationEffectEnabled,
        bool isNotificationSoundEnabled,
        bool isSpeechEnabled,
        bool isNotificationTopmostEnabled = false,
        int durationSeconds = 0,
        int repeatCounts = 1,
        bool isRollingEnabled = false)
    {
        var count = isRollingEnabled || repeatCounts < 1 ? 1 : repeatCounts;
        return Enumerable.Range(0, count)
            .Select(_ => BuildNotificationRequest(
                providerSettings,
                title,
                message,
                isNotificationEffectEnabled,
                isNotificationSoundEnabled,
                isSpeechEnabled,
                isNotificationTopmostEnabled,
                durationSeconds,
                repeatCounts,
                isRollingEnabled))
            .ToList();
    }

    internal static NotificationRequest BuildNotificationRequest(
        NotificationSettings providerSettings,
        string title,
        string message,
        bool isNotificationEffectEnabled,
        bool isNotificationSoundEnabled,
        bool isSpeechEnabled,
        bool isNotificationTopmostEnabled = false,
        int durationSeconds = 0,
        int repeatCounts = 1,
        bool isRollingEnabled = false)
    {
        // ClassIsland 的提供方设置优先于请求设置；RemoteCI 的选项来自每条消息，因此禁用前者。
        providerSettings.IsSettingsEnabled = false;
        // 与 ClassIsland 集控 SendNotification 一致的归一化：显示时长至少 1 秒，重复至少 1 次。
        var duration = durationSeconds <= 0 ? DefaultDurationSeconds : durationSeconds;
        var repeats = repeatCounts < 1 ? 1 : repeatCounts;
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
                : isRollingEnabled
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
                IsNotificationEffectEnabled = isNotificationEffectEnabled,
                IsNotificationSoundEnabled = isNotificationSoundEnabled,
                IsSpeechEnabled = isSpeechEnabled,
                IsNotificationTopmostEnabled = isNotificationTopmostEnabled,
            },
        };
    }

    /// <summary>与 ClassIsland 集控 SendNotification 一致的默认显示秒数。</summary>
    internal const int DefaultDurationSeconds = 5;
}
