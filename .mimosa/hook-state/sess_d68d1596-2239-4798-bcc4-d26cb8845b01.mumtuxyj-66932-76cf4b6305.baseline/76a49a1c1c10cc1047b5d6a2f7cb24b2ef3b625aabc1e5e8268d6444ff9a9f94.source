using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 单台 ClassIsland 设备上报的软件清单。应用表示 ClassIsland 主程序，插件表示宿主当前已加载插件。
/// </summary>
public sealed class SoftwareInventory
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("operatingSystem")]
    public string OperatingSystem { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("applications")]
    public List<SoftwarePackageInfo> Applications { get; set; } = [];

    [JsonPropertyName("plugins")]
    public List<SoftwarePackageInfo> Plugins { get; set; } = [];

    /// <summary>最近一次远程升级或刷新结果；旧版插件不上报该字段。</summary>
    [JsonPropertyName("lastUpdate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SoftwareUpdateStatus? LastUpdate { get; set; }
}

/// <summary>应用或插件的最小版本信息，供 WebUI 统一展示和筛选。</summary>
public sealed class SoftwarePackageInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("isUpdateAvailable")]
    public bool IsUpdateAvailable { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>当前宿主是否允许 RemoteCI 发起升级；旧版宿主可能只能查看版本。</summary>
    [JsonPropertyName("canUpgrade")]
    public bool CanUpgrade { get; set; }
}

public enum SoftwareUpdateOperation
{
    None = 0,
    InventoryRefresh = 1,
    Plugins = 2,
    ClassIsland = 3,
}

public enum SoftwareUpdateState
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>远程软件升级的状态快照；命令立即返回，实际下载与部署在设备后台执行。</summary>
public sealed class SoftwareUpdateStatus
{
    [JsonPropertyName("operation")]
    public SoftwareUpdateOperation Operation { get; set; }

    [JsonPropertyName("state")]
    public SoftwareUpdateState State { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("completedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>远程升级命令的可选参数；插件列表为空时表示升级全部可用插件。</summary>
public sealed class SoftwareUpgradeRequest
{
    [JsonPropertyName("pluginIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PluginIds { get; set; }

    [JsonPropertyName("force")]
    public bool Force { get; set; }
}
