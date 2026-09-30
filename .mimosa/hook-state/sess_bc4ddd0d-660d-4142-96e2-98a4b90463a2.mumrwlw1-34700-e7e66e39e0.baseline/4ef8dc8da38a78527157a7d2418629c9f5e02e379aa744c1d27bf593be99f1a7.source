using System.Reflection;
using System.Runtime.CompilerServices;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Models.Notification;
using HarmonyLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using HostNotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 旁路观察 ClassIsland 的通知发送入口，并把自动化及第三方插件通知转换为 RemoteCI 事件。
/// ClassIsland 的消费者接口会转移通知所有权，因此这里不能把手表注册成通知消费者。
/// </summary>
public sealed class ClassIslandNotificationBridge
{
    internal static readonly Guid AutomationProviderGuid = Guid.Parse("4B12F124-8585-43C7-AFC5-7BBB7CBE60D6");
    internal static readonly Guid RemoteCiProviderGuid = Guid.Parse("D680FD32-26F0-43EF-9E40-EF75252D1BD4");
    private const string HarmonyId = "com.remoteci.notification-bridge";
    private static readonly object SeenLock = new();
    private static readonly ConditionalWeakTable<HostNotificationRequest, object> SeenRequests = new();
    private static ClassIslandNotificationBridge? _active;

    private readonly Dictionary<Guid, NotificationProviderBase> _providers;
    private readonly Assembly? _classIslandAssembly;
    private readonly ILogger<ClassIslandNotificationBridge> _logger;
    private Harmony? _harmony;

    public ClassIslandNotificationBridge(
        IEnumerable<IHostedService> hostedServices,
        ILogger<ClassIslandNotificationBridge> logger)
    {
        _providers = hostedServices.OfType<NotificationProviderBase>().ToDictionary(x => x.ProviderGuid);
        _classIslandAssembly = _providers.GetValueOrDefault(AutomationProviderGuid)?.GetType().Assembly;
        _logger = logger;
    }

    public event Action<ClassEvent>? NotificationCaptured;

    public void Start()
    {
        if (_harmony is not null) return;

        try
        {
            var hostType = AccessTools.TypeByName("ClassIsland.Services.NotificationHostService");
            var showMethod = hostType is null
                ? null
                : AccessTools.Method(hostType, "ShowNotification", new[]
                {
                    typeof(HostNotificationRequest), typeof(Guid), typeof(Guid), typeof(bool), typeof(bool),
                });
            if (showMethod is null)
            {
                _logger.LogWarning("ClassIsland 通知入口不可用，自动化和第三方插件通知不会推送到手表");
                return;
            }

            _active = this;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(showMethod, postfix: new HarmonyMethod(typeof(ClassIslandNotificationBridge), nameof(AfterShowNotification)));

            var showChainedMethod = AccessTools.Method(hostType, "ShowChainedNotifications", new[]
            {
                typeof(HostNotificationRequest[]), typeof(Guid), typeof(Guid),
            });
            if (showChainedMethod is not null)
            {
                _harmony.Patch(showChainedMethod, postfix: new HarmonyMethod(typeof(ClassIslandNotificationBridge), nameof(AfterShowChainedNotifications)));
            }
        }
        catch (Exception ex)
        {
            // 通知桥接只是增强功能，打补丁失败绝不能拖垮插件主服务或 ClassIsland 启动流程。
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
            if (ReferenceEquals(_active, this)) _active = null;
            _logger.LogWarning(ex, "ClassIsland 通知桥接补丁失败，自动化和第三方插件通知不会推送到手表");
        }
    }

    public void Stop()
    {
        if (_harmony is null) return;
        _harmony.UnpatchAll(HarmonyId);
        _harmony = null;
        if (ReferenceEquals(_active, this)) _active = null;
    }

    // 参数名与 ClassIsland 的内部方法保持一致，Harmony 按名称注入原始参数。
    // 语义（对照 ClassIsland 源码 NotificationHostService.ShowNotification）：
    //   isPlayed=false → 宿主负责播放；isPlayed=true → 提供方已自行播放、宿主仅入队记录。
    // 两种情况用户都真实看到了提醒，都应转发；重复转发由 SeenRequests 按请求实例去重，
    // 不能依赖 isPlayed 过滤——否则未来提供方自行播放的通知会被手表漏掉。
    private static void AfterShowNotification(HostNotificationRequest request, Guid providerGuid, bool isPlayed)
    {
        _active?.Capture(request, providerGuid);
    }

    private static void AfterShowChainedNotifications(HostNotificationRequest[] requests, Guid providerGuid)
    {
        foreach (var request in requests) _active?.Capture(request, providerGuid);
    }

    private void Capture(HostNotificationRequest request, Guid providerGuid)
    {
        try
        {
            // 全局通知关闭时宿主不会初始化请求，此时不应让手表显示一条桌面端未显示的通知。
            if (request.CompletedToken.IsCancellationRequested) return;
            lock (SeenLock)
            {
                if (SeenRequests.TryGetValue(request, out _)) return;
                SeenRequests.Add(request, new object());
            }

            var kind = ClassifyProvider(providerGuid, _providers.GetValueOrDefault(providerGuid)?.GetType().Assembly, _classIslandAssembly);
            if (kind is null) return;

            var title = ExtractText(request.MaskContent);
            var message = request.OverlayContent is null ? null : ExtractText(request.OverlayContent);
            NotificationCaptured?.Invoke(new ClassEvent
            {
                Event = kind.Value,
                Subject = string.IsNullOrWhiteSpace(title)
                    ? kind == ClassEventKind.AutomationNotification ? "ClassIsland 自动化" : "ClassIsland 插件通知"
                    : title,
                Message = string.IsNullOrWhiteSpace(message) ? title : message,
            });
        }
        catch (Exception ex)
        {
            // 捕获失败绝不能影响 ClassIsland 原本的通知显示流程。
            _logger.LogWarning(ex, "转发 ClassIsland 通知到 RemoteCI 失败");
        }
    }

    internal static ClassEventKind? ClassifyProvider(Guid providerGuid, Assembly? providerAssembly, Assembly? classIslandAssembly)
    {
        if (providerGuid == RemoteCiProviderGuid) return null;
        if (providerGuid == AutomationProviderGuid) return ClassEventKind.AutomationNotification;
        return providerAssembly is not null && classIslandAssembly is not null && providerAssembly != classIslandAssembly
            ? ClassEventKind.PluginNotification
            : null;
    }

    internal static string? ExtractText(NotificationContent content)
    {
        if (!string.IsNullOrWhiteSpace(content.SpeechContent)) return content.SpeechContent.Trim();
        if (content.Content is string text) return text.Trim();
        return content.Content?.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(content.Content) as string;
    }
}
