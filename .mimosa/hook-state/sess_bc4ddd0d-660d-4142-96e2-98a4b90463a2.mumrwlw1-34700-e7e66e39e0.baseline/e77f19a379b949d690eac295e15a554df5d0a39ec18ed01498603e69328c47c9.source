using System.Net;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class PluginNetworkInfoProviderTests
{
    [Fact]
    public void NormalizeAddresses_KeepsUsableUniqueAddressesAndPrefersIpv4()
    {
        var addresses = PluginNetworkInfoProvider.NormalizeAddresses([
            IPAddress.Loopback,
            IPAddress.Any,
            IPAddress.Parse("169.254.3.4"),
            IPAddress.IPv6Loopback,
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("fd00::10"),
            IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("192.168.1.20"),
        ]);

        Assert.Equal(["192.168.1.20", "fd00::10"], addresses);
    }
}
