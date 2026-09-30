using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

/// <summary>
/// 覆盖宿主服务不可用（测试环境未注入 ClassIsland 宿主）时的回落行为：
/// 通知播放状态取 false、主界面可见性取 true、清除提醒明确报“不支持”。
/// </summary>
public sealed class ClassIslandHostControlServiceTests
{
    private static ClassIslandHostControlService Create() =>
        new(null, null, NullLogger<ClassIslandHostControlService>.Instance);

    [Fact]
    public void IsNotificationPlaying_IsFalseWhenHostServiceUnavailable() =>
        Assert.False(Create().IsNotificationPlaying);

    [Fact]
    public void IsMainMenuVisible_DefaultsToTrueWhenHostSettingsUnavailable() =>
        Assert.True(Create().IsMainMenuVisible);

    [Fact]
    public void IsSleepAvailable_MatchesOperatingSystem() =>
        Assert.Equal(OperatingSystem.IsWindows(), Create().IsSleepAvailable);

    [Fact]
    public async Task ClearNotificationsAsync_ThrowsNotSupportedWhenHostUnavailable() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Create().ClearNotificationsAsync());

    [Fact]
    public void CancelPendingPowerActions_IsSafeWhenNothingIsPending() =>
        Create().CancelPendingPowerActions();
}
