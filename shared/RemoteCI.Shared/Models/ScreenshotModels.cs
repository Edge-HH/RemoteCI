using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 屏幕截图命令参数（CaptureScreen）：服务端先登记一条待上传记录，再把编号与一次性上传凭据随命令下发；
/// 插件截屏后以自身插件令牌加 X-RemoteCI-Upload-Key 请求头上传 PNG，上传成功后才回执成功。
/// </summary>
public sealed class ScreenshotRequest
{
    /// <summary>截图 PNG 的协议上限；服务端可另行配置更小的值。</summary>
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int DefaultMaxWidth = 1920;
    public const int MinMaxWidth = 320;
    public const int MaxMaxWidth = 7680;
    public const string UploadKeyHeader = "X-RemoteCI-Upload-Key";

    /// <summary>服务端截图编号（32 位十六进制）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("uploadKey")]
    public string UploadKey { get; set; } = string.Empty;

    /// <summary>截图后是否在教室端弹出轻提示（由服务端系统设置决定）。</summary>
    [JsonPropertyName("notify")]
    public bool Notify { get; set; }

    /// <summary>截图宽度上限（像素），超出时等比缩小；多显示器拼接后的整幅宽度同样受此限制。</summary>
    [JsonPropertyName("maxWidth")]
    public int MaxWidth { get; set; } = DefaultMaxWidth;

    /// <summary>校验参数；通过时返回 null，否则返回中文错误说明。</summary>
    public static string? Validate(ScreenshotRequest? request)
    {
        if (request is null)
            return "缺少截图参数";
        if (!IsId(request.Id))
            return "截图编号无效";
        if (string.IsNullOrWhiteSpace(request.UploadKey) || request.UploadKey.Length > 128)
            return "上传凭据无效";
        if (request.MaxWidth is < MinMaxWidth or > MaxMaxWidth)
            return $"截图宽度上限必须在 {MinMaxWidth}-{MaxMaxWidth} 像素之间";
        return null;
    }

    public static bool IsId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>插件截图成功后放在 CommandResult.Data 中的 JSON。</summary>
public sealed class ScreenshotResult
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
