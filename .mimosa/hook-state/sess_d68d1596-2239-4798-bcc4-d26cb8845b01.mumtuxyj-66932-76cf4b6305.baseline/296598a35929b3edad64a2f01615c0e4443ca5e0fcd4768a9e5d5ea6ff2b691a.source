using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 服务端推送给在线手表的全局设置快照。
/// 目前包含“强制在标题显示发送人”开关，用于手表端提示与发送行为对齐。
/// </summary>
public sealed class SettingsSync
{
    [JsonPropertyName("forceSenderInTitle")]
    public bool ForceSenderInTitle { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
