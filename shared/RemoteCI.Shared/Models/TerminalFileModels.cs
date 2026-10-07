using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 远程终端的单条命令执行请求。每次执行相互独立（等价于 cmd /d /c），进程之间不保留状态；
/// 输出与超时都有硬上限，保证回执能落在服务端 15 秒命令窗口内。
/// </summary>
public sealed class TerminalCommandRequest
{
    /// <summary>命令文本上限，与 cmd 命令行长度约束保持同一数量级。</summary>
    public const int MaxCommandLength = 4000;

    /// <summary>标准输出与标准错误合并后的字符上限，超出截断并注明。</summary>
    public const int MaxOutputChars = 64 * 1024;

    /// <summary>默认与最大执行秒数；服务端命令回执窗口 15 秒，必须留出网络往返余量。</summary>
    public const int DefaultTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 10;

    /// <summary>工作目录路径上限，防止异常超长输入。</summary>
    public const int MaxWorkingDirectoryLength = 260;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>可选工作目录；缺省使用设备用户目录，目录不存在时返回 INVALID_REQUEST。</summary>
    [JsonPropertyName("workingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutSeconds { get; set; }
}

/// <summary>文件分发的保存位置；只允许写入固定的用户可见目录，不接受任意路径。</summary>
public enum FileTargetFolder
{
    Desktop = 1,
    Downloads = 2,
    Documents = 3,
}

/// <summary>
/// 分发一个文件到设备。内容以内联 Base64 经 WebSocket 传输（解码后上限 10 MiB，与语音共用 16 MiB 信封），
/// 保存目录只允许 FileTargetFolder 列出的用户文件夹，文件名经过净化以防止路径穿越；
/// 默认不覆盖已有文件，而是自动追加序号。
/// </summary>
public sealed class FileDistributionRequest
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFileNameLength = 200;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("contentBase64")]
    public string ContentBase64 { get; set; } = string.Empty;

    [JsonPropertyName("targetFolder")]
    public FileTargetFolder TargetFolder { get; set; } = FileTargetFolder.Desktop;

    /// <summary>true 时覆盖同名文件；false 时自动追加序号，绝不覆盖已有文件。</summary>
    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; }

    /// <summary>
    /// 解码并校验文件内容与文件名；通过时返回 true 并给出文件字节。
    /// 服务端转发前与插件落盘前共用同一口径，避免两端校验不一致。
    /// </summary>
    public static bool TryDecode(FileDistributionRequest? request, out byte[] content)
    {
        content = [];
        if (request is null ||
            string.IsNullOrWhiteSpace(request.ContentBase64) ||
            request.ContentBase64.Length > (MaxFileBytes + 2) / 3 * 4)
            return false;
        try
        {
            content = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        return content.Length is > 0 and <= MaxFileBytes;
    }

    /// <summary>净化文件名：剥掉目录部分与非法字符，拦截 Windows 保留设备名；无效时返回 null。</summary>
    public static string? SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = raw.Trim();
        // 剥离任何形式的目录部分（含 Windows 与 Unix 分隔符），只保留文件名本身。
        name = name.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(ch => !invalid.Contains(ch) && ch > 31).ToArray()).Trim();
        name = name.TrimEnd(' ', '.');
        if (name.Length == 0) return null;
        if (name.Length > MaxFileNameLength) name = name[..MaxFileNameLength].TrimEnd(' ', '.');
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (ReservedDeviceNames.Contains(stem)) name = "_" + name;
        return name;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
