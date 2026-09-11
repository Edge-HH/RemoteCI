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
