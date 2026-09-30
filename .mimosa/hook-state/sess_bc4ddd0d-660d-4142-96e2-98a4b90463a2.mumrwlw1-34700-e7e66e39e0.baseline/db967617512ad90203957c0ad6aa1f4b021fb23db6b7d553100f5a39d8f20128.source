using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>插件或手表对当前连接上报的软件版本与能力。</summary>
public sealed class PeerCapabilities
{
    [JsonPropertyName("softwareVersion")]
    public string SoftwareVersion { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; set; } = [];
}

/// <summary>供手表计算本地、服务端与当前主插件能力交集的快照。</summary>
public sealed class CapabilitiesSync
{
    [JsonPropertyName("server")]
    public PeerCapabilities Server { get; set; } = new();

    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PeerCapabilities? Plugin { get; set; }
}
