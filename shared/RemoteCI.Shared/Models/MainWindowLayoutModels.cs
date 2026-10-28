using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>主界面文件的写入方式：覆盖设备当前使用的文件，或新建一个文件。</summary>
public enum MainWindowLayoutApplyMode
{
    OverwriteCurrent = 1,
    CreateNew = 2,
}

/// <summary>
/// 上传一份 ClassIsland 主界面文件（Config/ComponentLayouts/*.json，即组件配置方案）。
/// 只写入主界面文件目录，不执行任意文件写入。
/// </summary>
public sealed class MainWindowLayoutApplyRequest
{
    [JsonPropertyName("layoutJson")]
    public string LayoutJson { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public MainWindowLayoutApplyMode Mode { get; set; } = MainWindowLayoutApplyMode.OverwriteCurrent;

    /// <summary>新建时的文件名（不含 .json）；覆盖当前文件时忽略。</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>新建后是否立即使用该主界面文件；覆盖当前文件时总是立即生效。</summary>
    [JsonPropertyName("activate")]
    public bool Activate { get; set; } = true;
}

/// <summary>读取主界面文件的回执数据，序列化后放在 CommandResult.Data。</summary>
public sealed class MainWindowLayoutSnapshot
{
    /// <summary>当前使用的主界面文件名（不含 .json）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("layoutJson")]
    public string LayoutJson { get; set; } = string.Empty;

    /// <summary>设备上全部主界面文件名。</summary>
    [JsonPropertyName("layouts")]
    public List<string> Layouts { get; set; } = [];

    /// <summary>设备处于 ClassIsland 集控模式时主界面由集控下发，此时只能读取、不能上传。</summary>
    [JsonPropertyName("isManagementMode")]
    public bool IsManagementMode { get; set; }
}

/// <summary>服务端与插件共用的主界面文件校验。</summary>
public static class MainWindowLayoutRules
{
    public const int MaxUtf8Bytes = 2 * 1024 * 1024;
    public const int MaxNameLength = 64;

    /// <summary>校验上传请求的形态；通过返回 null。新建时同时规范化 <see cref="MainWindowLayoutApplyRequest.Name"/>。</summary>
    public static string? Validate(MainWindowLayoutApplyRequest? request)
    {
        if (request is null) return "缺少主界面文件参数";
        if (!Enum.IsDefined(request.Mode)) return "未知的主界面文件写入方式";
        if (ValidateJson(request.LayoutJson) is { } jsonError) return jsonError;
        if (request.Mode != MainWindowLayoutApplyMode.CreateNew) return null;
        if (NormalizeName(request.Name, out var name) is { } nameError) return nameError;
        request.Name = name;
        return null;
    }

    /// <summary>主界面文件必须是 ClassIsland 2.x 组件配置：包含 Lines 数组的 JSON 对象。</summary>
    public static string? ValidateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "主界面文件不能为空";
        if (Encoding.UTF8.GetByteCount(json) > MaxUtf8Bytes) return "主界面文件不能超过 2 MB";
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { return $"主界面文件不是有效的 JSON：{ex.Message}"; }
        if (node is not JsonObject root ||
            root.FirstOrDefault(entry => entry.Key.Equals("Lines", StringComparison.OrdinalIgnoreCase)).Value is not JsonArray)
            return "不是 ClassIsland 主界面文件（缺少 Lines 主界面行）";
        return null;
    }

    /// <summary>新建文件名只能是单个文件名；可带 .json 后缀，返回不含后缀的名称。</summary>
    public static string? NormalizeName(string? raw, out string name)
    {
        name = raw?.Trim() ?? string.Empty;
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5].Trim();
        if (name.Length == 0) return "请填写新主界面文件名";
        if (name.Length > MaxNameLength) return $"主界面文件名不能超过 {MaxNameLength} 个字符";
        if (name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            name.Any(char.IsControl) || name.EndsWith('.'))
            return "主界面文件名包含非法字符";
        return null;
    }
}
