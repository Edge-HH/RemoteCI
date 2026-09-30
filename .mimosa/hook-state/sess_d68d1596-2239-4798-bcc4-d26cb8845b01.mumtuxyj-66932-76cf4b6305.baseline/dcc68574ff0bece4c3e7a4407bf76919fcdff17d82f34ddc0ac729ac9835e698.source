using RemoteCI.Server.Pages;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ControlModelTests
{
    [Fact]
    public void VolumeIncreaseWhileMuted_CombinesLevelAndUnmute()
    {
        var request = ControlModel.CreateVolumeRequest(68, unmute: true);

        Assert.Equal(68, request.Level);
        Assert.False(request.Muted);
    }

    [Fact]
    public void NormalVolumeChange_DoesNotOverwriteMuteState()
    {
        var request = ControlModel.CreateVolumeRequest(32, unmute: false);

        Assert.Equal(32, request.Level);
        Assert.Null(request.Muted);
    }
}
