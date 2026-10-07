using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using System.Text.Json;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CommandPermissionsTests
{
    [Theory]
    [InlineData(CommandKind.SendNotification, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.ClearNotifications, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.TeacherComing, UserPermissions.TeacherComing)]
    [InlineData(CommandKind.SetMainMenuVisibility, UserPermissions.MainMenuControl)]
    [InlineData(CommandKind.Power, UserPermissions.PowerControl)]
    [InlineData(CommandKind.Volume, UserPermissions.PowerControl)]
    [InlineData(CommandKind.RefreshSoftwareInventory, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.UpgradePlugins, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.UpgradeClassIsland, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.InstallPlugins, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.UninstallPlugins, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.SetPluginEnabled, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.SetPluginManagementPolicy, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.DistributeProfile, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.UpdateTimeLayout, UserPermissions.ManageUsers)]
    [InlineData(CommandKind.JoinManagement, UserPermissions.ManageUsers)]
    public void ControlCommands_UseExpectedPermission(CommandKind command, UserPermissions expected)
    {
        Assert.Equal(expected, CommandPermissions.Required(command));
    }

    [Fact]
    public void RunExtension_UsesIndependentExtensionAuthorization()
    {
        // 扩展命令由 RunExtensions 和逐扩展策略统一授权，不走普通命令的静态权限表。
        Assert.Equal(UserPermissions.None, CommandPermissions.Required(CommandKind.RunExtension));
    }

    [Fact]
    public void BreakingPermissionModelUsesProtocolVersionThree()
    {
        Assert.Equal(3, Protocol.Version);
        Assert.Equal("REMOTECI_DISCOVER_V3", Protocol.LanDiscoveryRequest);
        var json = JsonSerializer.Serialize(new Envelope { Type = Protocol.MessageTypeStatePush });
        Assert.Contains("\"protocolVersion\":3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftwareUpgradeCapabilitiesAreOptInAndNamed()
    {
        Assert.DoesNotContain(RemoteCiCapabilities.SoftwareInventory, RemoteCiCapabilities.Baseline);
        Assert.DoesNotContain(RemoteCiCapabilities.SoftwareUpgradePlugins, RemoteCiCapabilities.Baseline);
        Assert.DoesNotContain(RemoteCiCapabilities.SoftwareUpgradeClassIsland, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.SoftwareInventory, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.SoftwareUpgradePlugins, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.SoftwareUpgradeClassIsland, RemoteCiCapabilities.Current);
        Assert.NotEqual("未知能力", RemoteCiCapabilities.ChineseName(RemoteCiCapabilities.SoftwareUpgradeClassIsland));
        Assert.DoesNotContain(RemoteCiCapabilities.PluginInstall, RemoteCiCapabilities.Baseline);
        Assert.DoesNotContain(RemoteCiCapabilities.ManagementJoin, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.PluginInstall, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.ProfileDistribute, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.TimeLayoutUpdate, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.ManagementJoin, RemoteCiCapabilities.Current);
    }
    [Fact]
    public void EveryBaselineCapabilityHasChineseDiagnosticName()
    {
        Assert.All(RemoteCiCapabilities.Baseline, capability =>
            Assert.NotEqual("未知能力", RemoteCiCapabilities.ChineseName(capability)));
    }
}
