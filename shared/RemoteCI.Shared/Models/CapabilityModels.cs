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

    /// <summary>
    /// 兼容旧客户端的单插件能力：接收方可访问班级中最早接入的插件。
    /// 新客户端应优先按当前班级读取 <see cref="ClassPlugins"/>。
    /// </summary>
    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PeerCapabilities? Plugin { get; set; }

    /// <summary>
    /// 接收方可访问的每个班级当前主插件（该班最早接入的健康插件）的能力；
    /// 未出现的班级表示插件离线。旧版服务端不下发该字段。
    /// </summary>
    [JsonPropertyName("classPlugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClassPluginCapabilities>? ClassPlugins { get; set; }
}

/// <summary>某个班级当前主插件的能力。</summary>
public sealed class ClassPluginCapabilities
{
    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("plugin")]
    public PeerCapabilities Plugin { get; set; } = new();
}
