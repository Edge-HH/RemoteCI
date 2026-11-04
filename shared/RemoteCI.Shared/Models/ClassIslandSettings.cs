using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 部分更新 ClassIsland 设置的命令载荷（命令值 38 SetClassIslandSettings）。
/// <see cref="Values"/> 只包含本次要修改的设置项，未出现的项保持设备当前值，绝不整体覆盖。
/// 键为 ClassIsland <c>Settings</c> 的属性名（即 Settings.json 的 JSON 键，PascalCase），
/// 且必须出现在 <see cref="ClassIslandSettingsCatalog"/> 中。
/// </summary>
public sealed class ClassIslandSettingsRequest
{
    /// <summary>每次最多修改的设置项数量。</summary>
    public const int MaxValues = 128;

    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement>? Values { get; set; }
}

/// <summary>设备当前 ClassIsland 设置的读取结果（命令值 37 ReadClassIslandSettings 的 CommandResult.data）。</summary>
public sealed class ClassIslandSettingsSnapshot
{
    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement>? Values { get; set; }
}
