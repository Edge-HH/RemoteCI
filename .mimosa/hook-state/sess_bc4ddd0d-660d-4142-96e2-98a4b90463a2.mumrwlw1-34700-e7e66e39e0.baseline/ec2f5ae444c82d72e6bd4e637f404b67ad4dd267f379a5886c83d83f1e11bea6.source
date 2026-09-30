using System.Reflection;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;
using HostNotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace RemoteCI.Plugin.Tests;

public sealed class ClassIslandNotificationBridgeTests
{
    private static readonly FieldInfo ActiveField = typeof(ClassIslandNotificationBridge)
        .GetField("_active", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo AfterShowMethod = typeof(ClassIslandNotificationBridge)
        .GetMethod("AfterShowNotification", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void ClassifiesAutomationAndThirdPartyProvidersWithoutRelayingRemoteCiOrBuiltIns()
    {
        var hostAssembly = typeof(NotificationContent).Assembly;
        var pluginAssembly = typeof(ClassIslandNotificationBridgeTests).Assembly;

        Assert.Equal(
            ClassEventKind.AutomationNotification,
            ClassIslandNotificationBridge.ClassifyProvider(
                ClassIslandNotificationBridge.AutomationProviderGuid,
                hostAssembly,
                hostAssembly));
        Assert.Equal(
            ClassEventKind.PluginNotification,
            ClassIslandNotificationBridge.ClassifyProvider(Guid.NewGuid(), pluginAssembly, hostAssembly));
        Assert.Null(ClassIslandNotificationBridge.ClassifyProvider(Guid.NewGuid(), hostAssembly, hostAssembly));
        Assert.Null(ClassIslandNotificationBridge.ClassifyProvider(
            ClassIslandNotificationBridge.RemoteCiProviderGuid,
            pluginAssembly,
            hostAssembly));
    }

    [Fact]
    public void ExtractTextPrefersSpeechAndSupportsTemplateText()
    {
        var withSpeech = NotificationContent.CreateSimpleTextContent("正文");
        var withTemplate = new NotificationContent(new TemplateData { Text = "模板正文" });

        Assert.Equal("正文", ClassIslandNotificationBridge.ExtractText(withSpeech));
        Assert.Equal("模板正文", ClassIslandNotificationBridge.ExtractText(withTemplate));
    }

    [Fact]
    public void Capture_FiresForBothHostPlayedAndProviderPlayedRequests()
    {
        // 语义（对照 ClassIsland 源码）：isPlayed=false 由宿主播放、true 由提供方自行播放，
        // 两种情况用户都真实看到了提醒，都必须转发；去重由请求实例完成。
        var bridge = new ClassIslandNotificationBridge(
            Array.Empty<IHostedService>(), NullLogger<ClassIslandNotificationBridge>.Instance);
        var events = new List<ClassEvent>();
        bridge.NotificationCaptured += events.Add;
        ActiveField.SetValue(null, bridge);
        try
        {
            var playedByHost = new HostNotificationRequest
            {
                MaskContent = NotificationContent.CreateSimpleTextContent("宿主播放"),
            };
            var playedByProvider = new HostNotificationRequest
            {
                MaskContent = NotificationContent.CreateSimpleTextContent("提供方播放"),
            };

            AfterShowMethod.Invoke(null, [playedByHost, ClassIslandNotificationBridge.AutomationProviderGuid, false]);
            AfterShowMethod.Invoke(null, [playedByProvider, ClassIslandNotificationBridge.AutomationProviderGuid, true]);

            Assert.Equal(2, events.Count);
            Assert.All(events, captured => Assert.Equal(ClassEventKind.AutomationNotification, captured.Event));
            Assert.Equal("宿主播放", events[0].Subject);
            Assert.Equal("提供方播放", events[1].Subject);

            // 同一请求实例重复触发只转发一次。
            AfterShowMethod.Invoke(null, [playedByHost, ClassIslandNotificationBridge.AutomationProviderGuid, false]);
            Assert.Equal(2, events.Count);

            // RemoteCI 自身提供方永不转发，避免循环。
            AfterShowMethod.Invoke(null,
                [
                    new HostNotificationRequest
                    {
                        MaskContent = NotificationContent.CreateSimpleTextContent("回环"),
                    },
                    ClassIslandNotificationBridge.RemoteCiProviderGuid,
                    false,
                ]);
            Assert.Equal(2, events.Count);
        }
        finally
        {
            if (ReferenceEquals(ActiveField.GetValue(null), bridge)) ActiveField.SetValue(null, null);
        }
    }

    private sealed class TemplateData
    {
        public string Text { get; init; } = string.Empty;
    }
}
