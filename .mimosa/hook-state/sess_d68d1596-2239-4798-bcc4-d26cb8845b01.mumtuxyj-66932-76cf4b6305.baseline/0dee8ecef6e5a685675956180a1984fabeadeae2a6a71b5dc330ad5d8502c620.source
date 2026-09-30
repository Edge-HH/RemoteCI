using System.Text.Json;
using Avalonia;
using ClassIsland.Core.Controls;
using RemoteCI.Plugin.Views;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class VoiceMessageTests
{
    [Fact]
    public void IconButtonTemplateCanBeAppliedAfterContentGetsItsLogicalParent()
    {
        var button = VoiceMessageWindow.CreateIconButton(new FluentIcon("\uEDB8", 20), "播放");

        button.ApplyTemplate();
        var exception = Record.Exception(() => button.Measure(new Size(44, 44)));

        Assert.Null(exception);
    }

    [Fact]
    public void VoicePermissionIsIndependentAndAssignable()
    {
        Assert.Equal(UserPermissions.SendVoiceMessages, CommandPermissions.Required(CommandKind.SendVoiceMessage));
        Assert.False(UserPermissions.SendNotifications.HasFlag(CommandPermissions.Required(CommandKind.SendVoiceMessage)));
        Assert.True(RolePermissions.Assignable.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.True(RolePermissions.Effective(UserRole.Admin, UserPermissions.None).HasFlag(UserPermissions.SendVoiceMessages));
        Assert.DoesNotContain(RemoteCiCapabilities.VoiceMessageSend, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.VoiceMessageSend, RemoteCiCapabilities.Current);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(VoiceMessageRequest.MaxBytes, true)]
    [InlineData(VoiceMessageRequest.MaxBytes + 1, false)]
    [InlineData(VoiceMessageRequest.MaxBytes + 2, false)]
    public void ValidatesExactSampleAndDurationBounds(int length, bool valid)
    {
        var request = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(new byte[length]) };
        Assert.Equal(valid, VoiceMessageRequest.TryDecode(request, out var audio));
        Assert.Equal(valid ? length : 0, audio.Length);
    }

    [Fact]
    public void RejectsMissingCorruptAndUnsupportedAudio()
    {
        Assert.False(VoiceMessageRequest.TryDecode(null, out _));
        Assert.False(VoiceMessageRequest.TryDecode(new() { AudioBase64 = "invalid!" }, out _));
        Assert.False(VoiceMessageRequest.TryDecode(new() { Format = "mp3", AudioBase64 = "AAA=" }, out _));
    }

    [Fact]
    public void LongestAudioFitsEnvelopeEvenWithWorstCaseJsonEscaping()
    {
        var request = new VoiceMessageRequest { AudioBase64 = new string('+', VoiceMessageRequest.MaxBase64Length) };
        Assert.True(VoiceMessageRequest.TryDecode(request, out var audio));
        Assert.Equal(VoiceMessageRequest.MaxBytes, audio.Length);
        var wire = JsonSerializer.SerializeToUtf8Bytes(Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage, VoiceMessage = request,
            RequestedBy = new UserProfile { DisplayName = new string('名', 40) },
        }), JsonDefaults.Options);
        Assert.True(wire.Length <= VoiceMessageRequest.MaxEnvelopeBytes);
    }
}
