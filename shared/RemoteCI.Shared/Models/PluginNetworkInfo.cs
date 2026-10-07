using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>插件可供手表局域网直连的最新地址与端口。</summary>
public sealed class PluginNetworkInfo
{
    [JsonPropertyName("lanServerEnabled")]
    public bool LanServerEnabled { get; set; }

    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get; set; } = [];

    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>归属班级；缺省表示单班级部署的默认班级。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }
}
