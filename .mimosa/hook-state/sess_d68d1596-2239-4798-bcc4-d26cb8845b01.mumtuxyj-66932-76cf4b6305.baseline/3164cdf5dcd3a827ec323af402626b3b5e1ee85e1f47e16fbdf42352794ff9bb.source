using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>发现插件主机可供同一局域网设备访问的单播地址。</summary>
internal static class PluginNetworkInfoProvider
{
    public static PluginNetworkInfo Create(PluginSettings settings) => new()
    {
        LanServerEnabled = settings.EnableLanServer,
        Port = settings.LanServerPort,
        Addresses = DiscoverAddresses(),
    };

    internal static IReadOnlyList<string> NormalizeAddresses(IEnumerable<IPAddress> addresses) => addresses
        .Where(IsUsable)
        .Distinct()
        // Android 构造 IPv4 直连地址最稳定；保留 IPv6 作为多网卡环境的后备候选。
        .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .ThenBy(address => address.ToString(), StringComparer.Ordinal)
        .Select(address => address.ToString())
        .ToArray();

    private static IReadOnlyList<string> DiscoverAddresses()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                    and not NetworkInterfaceType.Tunnel)
                .ToList();
            // 有默认网关的接口才真正承载手表流量；存在时剔除 WireGuard/VMnet/WSL 等无网关的虚拟网卡。
            var routed = candidates
                .Where(network => network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address is not null))
                .ToList();
            var selected = routed.Count > 0 ? routed : candidates;
            return NormalizeAddresses(selected
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address));
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsUsable(IPAddress address)
    {
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 169 || bytes[1] != 254;
        }
        return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
    }
}
