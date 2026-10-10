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

    /// <summary>归属班级；由服务端按插件凭据回填，未分配班级的设备为空。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }
}
