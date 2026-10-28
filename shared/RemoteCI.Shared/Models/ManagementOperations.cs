using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 批量控制页的统一操作类型。新增操作必须保持只追加，旧端收到未知操作时安全拒绝。
/// </summary>
public enum BatchOperationKind
{
    Notify = 1,
    ClearNotifications = 2,
    Power = 3,
    VoiceMessage = 4,
    UpdateTimeLayout = 10,
    DistributeProfile = 11,
    InstallPlugins = 20,
    UninstallPlugins = 21,
    SetPluginEnabled = 22,
    SetPluginManagementPolicy = 23,
    /// <summary>上传 .cipx 插件包到服务端，再分发到设备安装或更新。</summary>
    InstallPluginPackages = 24,
    RefreshSoftwareInventory = 30,
    UpgradePlugins = 31,
    UpgradeClassIsland = 32,
    JoinManagement = 40,
    RestartClassIsland = 41,
    ExecuteTerminalCommand = 50,
    SendFile = 51,
    /// <summary>批量执行某个已注册的扩展功能。</summary>
    RunExtension = 60,
}

public enum PluginActionKind
{
    Install = 1,
    Uninstall = 2,
    Enable = 3,
    Disable = 4,
}

/// <summary>插件管理命令：安装、卸载、启用或禁用一组插件。</summary>
public sealed class PluginManagementRequest
{
    [JsonPropertyName("action")]
    public PluginActionKind Action { get; set; }

    [JsonPropertyName("pluginIds")]
    public List<string> PluginIds { get; set; } = [];

    /// <summary>操作完成后是否请求 ClassIsland 自动重启；下载类操作默认等待下载完成再重启。</summary>
    [JsonPropertyName("restartAfter")]
    public bool RestartAfter { get; set; } = true;
}

public enum PluginPackageInstallMode
{
    /// <summary>安装插件；已安装的显式插件包会被覆盖安装，已安装的依赖包跳过。</summary>
    Install = 1,
    /// <summary>只更新本机已安装且插件包版本更高的插件（Force 时不比较版本）。</summary>
    Upgrade = 2,
}

/// <summary>
/// 服务端中转的一个 .cipx 插件包。插件按 transferId + downloadKey 从服务端文件中转分段下载并校验 SHA-256，
/// 再核对包内 manifest.yml 的插件 Id，最后放入 ClassIsland 插件包缓存，重启后由宿主安装。
/// </summary>
public sealed class PluginPackageRef
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>作为其他插件的必选依赖被一并下发；安装模式下本机已安装时跳过。</summary>
    [JsonPropertyName("isDependency")]
    public bool IsDependency { get; set; }

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("downloadKey")]
    public string DownloadKey { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>复用文件中转的参数校验口径。</summary>
    public FileDistributionRequest ToTransferRequest() => new()
    {
        FileName = $"{PluginId}.cipx",
        TransferId = TransferId,
        DownloadKey = DownloadKey,
        Size = Size,
        Sha256 = Sha256,
    };
}

/// <summary>InstallPluginPackages 命令参数：一组服务端中转的插件包。</summary>
public sealed class PluginPackageInstallRequest
{
    public const int MaxPackages = 32;

    [JsonPropertyName("mode")]
    public PluginPackageInstallMode Mode { get; set; } = PluginPackageInstallMode.Install;

    [JsonPropertyName("packages")]
    public List<PluginPackageRef> Packages { get; set; } = [];

    /// <summary>更新模式下不比较版本，强制覆盖已安装插件。</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }

    [JsonPropertyName("restartAfter")]
    public bool RestartAfter { get; set; } = true;
}

/// <summary>
/// RemoteCI 远程插件管理策略。ClassIsland 目前没有公开的宿主级插件管理策略 API，
/// 因此该策略只约束 RemoteCI 自己发起的安装/卸载，不阻止用户在 ClassIsland 本地设置页操作。
/// </summary>
public sealed class PluginManagementPolicyRequest
{
    [JsonPropertyName("allowRemoteInstall")]
    public bool AllowRemoteInstall { get; set; } = true;

    [JsonPropertyName("allowRemoteUninstall")]
    public bool AllowRemoteUninstall { get; set; } = true;
}

/// <summary>时间表中的一个时间点；时间使用 HH:mm，TimeType 与 ClassIsland 定义一致。</summary>
public sealed class TimeLayoutPointRequest
{
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "08:00";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "08:45";

    /// <summary>0=上课，1=课间，2=分割线，3=行动。</summary>
    [JsonPropertyName("timeType")]
    public int TimeType { get; set; }

    [JsonPropertyName("breakName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BreakName { get; set; }
}

/// <summary>新增或整体替换一张 ClassIsland 时间表。</summary>
public sealed class TimeLayoutUpdateRequest
{
    [JsonPropertyName("timeLayoutId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TimeLayoutId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "RemoteCI 时间表";

    /// <summary>是否把该时间表设为当前启用的时间表。</summary>
    [JsonPropertyName("activate")]
    public bool Activate { get; set; }

    [JsonPropertyName("points")]
    public List<TimeLayoutPointRequest> Points { get; set; } = [];

    [JsonPropertyName("restartAfter")]
    public bool RestartAfter { get; set; }
}

[Flags]
public enum ProfileDistributionSection
{
    None = 0,
    TimeLayouts = 1 << 0,
    ClassPlans = 1 << 1,
    Subjects = 1 << 2,
}

/// <summary>
/// 把一份 ClassIsland 档案 JSON 中的指定部分合并或替换到设备档案。
/// 只接收档案 JSON，不执行任意文件写入。
/// </summary>
public sealed class ProfileDistributionRequest
{
    [JsonPropertyName("profileJson")]
    public string ProfileJson { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    public ProfileDistributionSection Sections { get; set; } = ProfileDistributionSection.None;

    /// <summary>导入到新档案时使用的文件名（不含或可含 .json）。为空时使用源档案名称。</summary>
    [JsonPropertyName("importProfileName")]
    public string? ImportProfileName { get; set; }

    /// <summary>是否把选中的内容写入当前档案；关闭时会创建一个独立档案，默认关闭。</summary>
    [JsonPropertyName("replaceCurrentProfile")]
    public bool ReplaceCurrentProfile { get; set; }

    /// <summary>创建新档案后是否将其设为 ClassIsland 下次启动使用的档案，默认开启。</summary>
    [JsonPropertyName("enableImportedProfile")]
    public bool EnableImportedProfile { get; set; } = true;

    [JsonPropertyName("replaceExisting")]
    public bool ReplaceExisting { get; set; }

    [JsonPropertyName("restartAfter")]
    public bool RestartAfter { get; set; }
}

/// <summary>
/// 让设备加入 ClassIsland 内置集控。PresetJson 是管理员上传的集控配置文件
/// （ClassIsland 的 ManagementPreset.json，即 ManagementSettings 的 JSON 序列化），
/// 插件解析后写入宿主集控配置；ClassIdentity 由服务端按设备所属班级名自动填充。
/// </summary>
public sealed class ManagementJoinRequest
{
    [JsonPropertyName("presetJson")]
    public string PresetJson { get; set; } = string.Empty;

    /// <summary>集控配置文件中的 ID（ClassIdentity）；服务端按班级名自动填充。</summary>
    [JsonPropertyName("classIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassIdentity { get; set; }
}

/// <summary>批量控制逐设备结果；TargetId 对设备为插件凭据 Id，对班级级失败为班级 Id。</summary>
public sealed class BatchDeviceItemResult
{
    [JsonPropertyName("targetId")]
    public Guid TargetId { get; set; }

    [JsonPropertyName("connectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ConnectionId { get; set; }

    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("targetName")]
    public string TargetName { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>.cipx 插件包根目录 manifest.yml 的关键字段。</summary>
public sealed record PluginPackageManifest(string Id, string Name, string Version)
{
    public const string ManifestFileName = "manifest.yml";

    /// <summary>插件 Id 同时用作插件包文件名，只允许常见的标识字符，杜绝路径分隔符。</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    /// <summary>
    /// 读取插件包清单的顶层 id、name、version。ClassIsland 清单是扁平 YAML，这里只按顶层键逐行解析，
    /// 不引入 YAML 依赖；不是有效 zip、缺少清单或缺少 id 时返回 null。
    /// </summary>
    public static PluginPackageManifest? TryRead(Stream package)
    {
        try
        {
            using var archive = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry(ManifestFileName);
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var reader = new StreamReader(entry.Open());
            string? id = null, name = null, version = null;
            while (reader.ReadLine() is { } line)
            {
                // 缩进行属于嵌套对象（例如依赖列表），只读取顶层键。
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '-') continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("id", StringComparison.OrdinalIgnoreCase)) id ??= value;
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= value;
                else if (key.Equals("version", StringComparison.OrdinalIgnoreCase)) version ??= value;
            }
            return string.IsNullOrWhiteSpace(id)
                ? null
                : new PluginPackageManifest(id, string.IsNullOrWhiteSpace(name) ? id : name, version ?? string.Empty);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public static PluginPackageManifest? TryRead(string path)
    {
        using var stream = File.OpenRead(path);
        return TryRead(stream);
    }
}
