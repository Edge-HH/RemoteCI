using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>局域网扫描响应，仅用于让用户选择插件，不携带云端或认证信息。</summary>
public sealed class LanDiscoveryResponse
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Protocol.Version;

    [JsonPropertyName("instanceName")]
    public required string InstanceName { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>用户选中局域网插件后，由插件提供给手表的云端连接信息。</summary>
public sealed class ConnectionBootstrapInfo
{
    [JsonPropertyName("instanceName")]
    public required string InstanceName { get; set; }

    [JsonPropertyName("cloudServerUrl")]
    public required string CloudServerUrl { get; set; }
}
