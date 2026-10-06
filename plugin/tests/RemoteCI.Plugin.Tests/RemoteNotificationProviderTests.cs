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
            new NotificationSettings(), "标题", message, new RemoteNotificationOptions(true, true, true));

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
            new RemoteNotificationOptions(DurationSeconds: requested, IsRollingEnabled: true));

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
            new RemoteNotificationOptions(DurationSeconds: 5, RepeatCounts: repeatCounts, IsRollingEnabled: true));

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
    public void RollingExplicitlyDisabled_ShowsStaticBodyForOneDuration()
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "短通知",
            new RemoteNotificationOptions(IsSpeechEnabled: true, DurationSeconds: 8, RepeatCounts: 3, IsRollingEnabled: false));

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
            new RemoteNotificationOptions(RepeatCounts: repeatCounts, IsRollingEnabled: rolling));

        Assert.Equal(expectedCount, requests.Count);
        // ClassIsland 会改写请求状态，排队的每一条都必须是独立实例。
        Assert.Equal(expectedCount, requests.Distinct().Count());
    }

    [Fact]
    public void TopmostEnabled_PassesThroughToRequestSettings()
    {
        var request = RemoteNotificationProvider.BuildNotificationRequest(
            new NotificationSettings(), "标题", "正文",
            new RemoteNotificationOptions(IsNotificationTopmostEnabled: true));

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
            new RemoteNotificationOptions(true, true, true));

        Assert.False(providerSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationEffectEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.True(request.RequestNotificationSettings.IsSpeechEnabled);
        Assert.True(request.MaskContent.IsSpeechEnabled);
        Assert.True(request.OverlayContent!.IsSpeechEnabled);
    }

    [Theory]
    // 旧 V3 客户端省略 isRollingEnabled：保持升级前“正文滚动 N 遍、只排队一条”的行为。
    [InlineData("""{"title":"t","message":"m","repeatCounts":3}""", true, 1)]
    // 新客户端显式关闭滚动：静态正文，整条提醒依次显示 N 次。
    [InlineData("""{"title":"t","message":"m","repeatCounts":3,"isRollingEnabled":false}""", false, 3)]
    [InlineData("""{"title":"t","message":"m","repeatCounts":3,"isRollingEnabled":true}""", true, 1)]
    public void LegacyClientJson_KeepsRollingSemantics(string json, bool expectedRolling, int expectedQueued)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<RemoteCI.Shared.Models.NotificationRequest>(json)!;
        var options = RemoteNotificationOptions.From(request);

        Assert.Equal(expectedRolling, options.IsRollingEnabled);
        Assert.Equal(3, options.EffectiveRepeatCounts);
        var requests = RemoteNotificationProvider.BuildNotificationRequests(new NotificationSettings(), "t", "m", options);
        Assert.Equal(expectedQueued, requests.Count);
    }

    [Fact]
    public void OmittedRolling_IsNotWrittenBackAsFalse()
    {
        // 服务端反序列化后再转发给旧插件时，缺失字段必须继续缺失，不能被补成 false。
        var request = System.Text.Json.JsonSerializer.Deserialize<RemoteCI.Shared.Models.NotificationRequest>("""{"title":"t"}""")!;
        Assert.DoesNotContain("isRollingEnabled", System.Text.Json.JsonSerializer.Serialize(request));
    }

    [Fact]
    public void OutOfRangeOptions_AreClampedAtExecution()
    {
        // 越过协议上限的旧服务端或局域网请求在执行端被限幅，不会排队上千条提醒。
        var options = new RemoteNotificationOptions(DurationSeconds: 100_000, RepeatCounts: 1000, IsRollingEnabled: false);
        Assert.Equal(RemoteCI.Shared.Models.NotificationRequest.MaxDurationSeconds, options.EffectiveDurationSeconds);
        Assert.Equal(RemoteCI.Shared.Models.NotificationRequest.MaxRepeatCounts,
            RemoteNotificationProvider.BuildNotificationRequests(new NotificationSettings(), "t", "m", options).Count);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, 0, null)]
    [InlineData(3600, 10, null)]
    [InlineData(3601, 1, "持续时间")]
    [InlineData(-1, 1, "持续时间")]
    [InlineData(5, 11, "重复次数")]
    [InlineData(5, -1, "重复次数")]
    public void Validate_EnforcesDurationAndRepeatLimits(int? duration, int? repeats, string? expectedError)
    {
        var error = RemoteCI.Shared.Models.NotificationRequest.Validate(new RemoteCI.Shared.Models.NotificationRequest
        {
            DurationSeconds = duration,
            RepeatCounts = repeats,
        });
        if (expectedError is null) Assert.Null(error);
        else Assert.Contains(expectedError, error);
    }

    [Fact]
    public void Validate_RejectsOverlongMessage()
    {
        var request = new RemoteCI.Shared.Models.NotificationRequest
        {
            Message = new string('字', RemoteCI.Shared.Models.NotificationRequest.MaxMessageLength + 1),
        };
        Assert.NotNull(RemoteCI.Shared.Models.NotificationRequest.Validate(request));
    }
}
