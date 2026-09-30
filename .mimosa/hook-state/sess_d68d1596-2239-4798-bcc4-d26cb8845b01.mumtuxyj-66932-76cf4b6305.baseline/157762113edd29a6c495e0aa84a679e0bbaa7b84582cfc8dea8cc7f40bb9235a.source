using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 插件长期凭据的落盘存储：Windows 上用 DPAPI（CurrentUser 作用域 + 应用熵）加密，
/// 文件 ACL 收紧到当前用户；非 Windows 平台降级为明文（ClassIsland 目前只发布 Windows 版）。
/// 任何读写失败都按“凭据不存在”处理，绝不拖垮插件启动或配对流程。
/// </summary>
public sealed class CloudTokenStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteCI.CloudToken.v1");

    /// <summary>读取凭据；缺失、损坏或解密失败返回 null。</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var encrypted = Convert.FromBase64String(File.ReadAllText(path).Trim());
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser)
                : encrypted;
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            FormatException or CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>保存凭据；传入 null 时删除存储文件（吊销）。</summary>
    public void Save(string? token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var plain = Encoding.UTF8.GetBytes(token);
            var encrypted = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
                : plain;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, Convert.ToBase64String(encrypted));
            FileProtection.RestrictToCurrentUser(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            CryptographicException or PlatformNotSupportedException)
        {
            // 保存失败只影响重启后的免配对体验，不让插件流程中断。
        }
    }

    /// <summary>
    /// 从旧版 Settings.json 迁移明文 CloudToken：读取后写入加密存储并返回。
    /// 旧字段随下一次设置保存自然清除（CloudToken 已标记 JsonIgnore）。
    /// </summary>
    public static string? TryMigrateLegacyPlaintext(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("CloudToken", out var token) &&
                token.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(token.GetString()))
                return token.GetString();
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
