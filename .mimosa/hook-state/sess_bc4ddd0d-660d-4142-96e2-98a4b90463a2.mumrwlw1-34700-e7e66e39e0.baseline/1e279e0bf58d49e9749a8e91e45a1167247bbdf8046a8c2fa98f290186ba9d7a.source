using System.Text;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>局域网设备扫描的无状态协议判断，网络监听实现只负责收发数据报。</summary>
internal static class LanDiscoveryProtocol
{
    public static LanDiscoveryResponse? CreateResponse(
        ReadOnlySpan<byte> request,
        PluginSettings settings,
        string instanceName)
    {
        if (!request.SequenceEqual(Encoding.UTF8.GetBytes(Protocol.LanDiscoveryRequest))) return null;
        if (settings.LanServerPort is < 1 or > 65535) return null;
        return new LanDiscoveryResponse
        {
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? "RemoteCI 插件" : instanceName.Trim(),
            Port = settings.LanServerPort,
        };
    }

    public static Envelope CreateBootstrapEnvelope(PluginSettings settings, string instanceName) =>
        Envelope.ConnectionBootstrap(new ConnectionBootstrapInfo
        {
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? "RemoteCI 插件" : instanceName.Trim(),
            CloudServerUrl = settings.CloudServerUrl.Trim().TrimEnd('/'),
        });
}
