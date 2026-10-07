using ClassIsland.Shared.Models.Notification;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class RemoteNotificationProviderTests
{
    [Fact]
    public void VoiceMessage_EmphasizesWithoutSoundOrSpeechEvenWhenProviderEnablesThem()
    {
        var settings = new NotificationSettings { IsSettingsEnabled = true, IsNotificationSoundEnabled = true, IsSpeechEnabled = true };
        var request = RemoteNotificationProvider.BuildVoiceMessageNotification(settings, "来自小明的语音消息");
        Assert.False(settings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationEffectEnabled);
        Assert.False(request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.False(request.RequestNotificationSettings.IsSpeechEnabled);
        Assert.False(request.MaskContent.IsSpeechEnabled);
        Assert.Null(request.OverlayContent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void EmptyBody_OnlyShowsTitle(string message)
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", message, true, true, true);

        Assert.NotNull(request.MaskContent);
        Assert.True(request.MaskContent.IsSpeechEnabled);
        Assert.Null(request.OverlayContent);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    public void DurationSeconds_FallsBackToClassIslandDefault(int requested, int expected)
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "正文",
            isNotificationEffectEnabled: false,
            isNotificationSoundEnabled: false,
            isSpeechEnabled: false,
            durationSeconds: requested,
            isRollingEnabled: true);

        // 正文滚动 1 次时，总时长等于单条时长。
        Assert.Equal(TimeSpan.FromSeconds(expected), request.OverlayContent!.Duration);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void RepeatCounts_MultiplyOverlayDuration(int repeatCounts, int expectedRepeats)
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "正文",
            isNotificationEffectEnabled: false,
            isNotificationSoundEnabled: false,
            isSpeechEnabled: false,
            durationSeconds: 5,
            repeatCounts: repeatCounts,
            isRollingEnabled: true);

        // 总显示时长 = 持续时间 × 重复次数，与 ClassIsland 集控 SendNotification 一致。
        Assert.Equal(TimeSpan.FromSeconds(5 * expectedRepeats), request.OverlayContent!.Duration);
        var template = Assert.IsType<ClassIsland.Core.Controls.NotificationTemplates.RollingTextTemplate>(
            request.OverlayContent.Content);
        // RepeatCount 只保存在模板的数据上下文里，控件本身不暴露该属性。
        var data = Assert.IsType<ClassIsland.Core.Models.Notification.Templates.RollingTextTemplateData>(
            template.DataContext);
        Assert.Equal(expectedRepeats, data.RepeatCount);
    }

    [Fact]
    public void RollingDisabledByDefault_ShowsStaticBodyForOneDuration()
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "短通知",
            isNotificationEffectEnabled: false,
            isNotificationSoundEnabled: false,
            isSpeechEnabled: true,
            durationSeconds: 8,
            repeatCounts: 3);

        Assert.IsNotType<ClassIsland.Core.Controls.NotificationTemplates.RollingTextTemplate>(request.OverlayContent!.Content);
        Assert.Equal(TimeSpan.FromSeconds(8), request.OverlayContent.Duration);
        Assert.True(request.OverlayContent.IsSpeechEnabled);
    }

    [Theory]
    [InlineData(false, 3, 3)]
    [InlineData(false, 0, 1)]
    [InlineData(true, 3, 1)]
    public void RepeatCounts_QueueSeparateRequestsOnlyWhenNotRolling(bool rolling, int repeatCounts, int expectedCount)
    {
        var requests = RemoteNotificationProvider.BuildNotificationRequests(
            new NotificationSettings(), "标题", "正文",
            isNotificationEffectEnabled: false,
            isNotificationSoundEnabled: false,
            isSpeechEnabled: false,
            repeatCounts: repeatCounts,
            isRollingEnabled: rolling);

        Assert.Equal(expectedCount, requests.Count);
        // ClassIsland 会改写请求状态，排队的每一条都必须是独立实例。
        Assert.Equal(expectedCount, requests.Distinct().Count());
    }

    [Fact]
    public void TopmostEnabled_PassesThroughToRequestSettings()
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "正文",
            isNotificationEffectEnabled: false,
            isNotificationSoundEnabled: false,
            isSpeechEnabled: false,
            isNotificationTopmostEnabled: true);

        Assert.True(request.RequestNotificationSettings.IsNotificationTopmostEnabled);
    }

    [Fact]
    public void PerMessageOptionsOverrideEnabledProviderDefaults()
    {
        var providerSettings = new NotificationSettings
        {
            IsSettingsEnabled = true,
            IsNotificationEffectEnabled = false,
            IsNotificationSoundEnabled = false,
            IsSpeechEnabled = false,
        };

        var request = RemoteNotificationProvider.BuildNotificationRequest(
            providerSettings,
            "标题",
            "正文",
            isNotificationEffectEnabled: true,
            isNotificationSoundEnabled: true,
            isSpeechEnabled: true);

        Assert.False(providerSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationEffectEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.True(request.RequestNotificationSettings.IsSpeechEnabled);
        Assert.True(request.MaskContent.IsSpeechEnabled);
        Assert.True(request.OverlayContent!.IsSpeechEnabled);
    }
}
