using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>固定格式的内存语音，避免各平台编码器差异；不接受远程 URL 或文件路径。</summary>
public sealed class VoiceMessageRequest
{
    public const int SampleRate = 16000;
    public const int MaxSeconds = 60;
    public const int MaxBytes = SampleRate * 2 * MaxSeconds;
    public const int MaxBase64Length = (MaxBytes + 2) / 3 * 4;
    // 为 Base64 音频、JSON 转义和命令身份预留空间，接收端仍执行严格音频长度校验。
    public const int MaxEnvelopeBytes = 16 * 1024 * 1024;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "pcm_s16le_16000_mono";

    [JsonPropertyName("audioBase64")]
    public string AudioBase64 { get; set; } = string.Empty;

    public static bool TryDecode(VoiceMessageRequest? request, out byte[] audio)
    {
        audio = [];
        if (request is null || request.Format != "pcm_s16le_16000_mono" ||
            string.IsNullOrEmpty(request.AudioBase64) || request.AudioBase64.Length > MaxBase64Length)
            return false;
        try
        {
            var decoded = Convert.FromBase64String(request.AudioBase64);
            if (decoded.Length is < 2 or > MaxBytes || decoded.Length % 2 != 0) return false;
            audio = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}
