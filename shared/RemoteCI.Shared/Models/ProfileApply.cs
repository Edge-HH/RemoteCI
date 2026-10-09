using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>不能预选的档案应用方式；数字只追加，旧设备安全拒绝未知方式。</summary>
public enum ProfileApplyMode
{
    MergeCurrent = 1,
    ReplaceSections = 2,
    CreateAndActivate = 3,
    /// <summary>把载荷中按日期安排的临时层写入设备，不改动设备的常规课表与时间表。</summary>
    TempLayers = 4,
}

public sealed class ProfileApplyRequest
{
    /// <summary>服务端与 Windows 插件使用相同规则，提交前拒绝路径、保留设备名和非法文件名。</summary>
    public static string NormalizeImportName(string? input)
    {
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("创建档案时必须填写设备档案名");
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5].Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') ||
            name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)) || name.Length > 120)
            throw new ArgumentException("设备档案名包含非法字符或超过 120 字");
        var basename = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(basename, StringComparer.OrdinalIgnoreCase) ||
            basename.Length == 4 && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                   basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && basename[3] is >= '1' and <= '9')
            throw new ArgumentException("设备档案名是系统保留名称");
        return name;
    }

    [JsonPropertyName("profileJson")]
    public string ProfileJson { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    public ProfileDistributionSection Sections { get; set; }

    [JsonPropertyName("mode")]
    public ProfileApplyMode Mode { get; set; }

    [JsonPropertyName("importProfileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportProfileName { get; set; }

    [JsonPropertyName("restartAfter")]
    public bool RestartAfter { get; set; }

    /// <summary>临时层下发时，设备同日已有临时层或预定课表是否替换；为 false 时设备拒绝并说明日期。</summary>
    [JsonPropertyName("replaceExistingTempLayers")]
    public bool ReplaceExistingTempLayers { get; set; }
}
