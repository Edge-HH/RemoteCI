using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>
/// 手机扫码登录设置：系统管理员可指定二维码里的服务器地址（例如反向代理后的公网地址），
/// 未设置时使用当前 WebUI 访问地址。该地址只写入二维码，不影响服务端监听。
/// </summary>
public sealed class MobileLoginSettings(AppDbContext db)
{
    public const int MaxServerUrlLength = 512;

    /// <summary>扫码登录二维码的 URI 方案；只含服务器地址的旧二维码仍是普通 http(s) 地址。</summary>
    public const string QrScheme = "remoteci";

    public async Task<string?> GetServerUrlAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1).Select(x => x.MobileServerUrl).SingleAsync(ct);

    /// <summary>保存二维码服务器地址；空白表示恢复为当前访问地址。地址非法时抛出 <see cref="ArgumentException"/>。</summary>
    public async Task SetServerUrlAsync(string? value, CancellationToken ct = default)
    {
        var normalized = Normalize(value);
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.MobileServerUrl = normalized;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>规范化服务器地址：只接受不带查询串和片段的绝对 http/https 地址，去掉末尾斜杠。</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxServerUrlLength ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("服务器地址需为 http:// 或 https:// 开头的完整地址，且不能包含查询参数");
        return trimmed.TrimEnd('/');
    }

    /// <summary>网页扫码登录二维码内容：服务器地址与挑战码；手机 App 扫码确认后由显示二维码的浏览器登录。</summary>
    public static string BuildWebLoginQrPayload(string serverUrl, string code) =>
        $"{QrScheme}://weblogin?server={Uri.EscapeDataString(serverUrl)}&code={Uri.EscapeDataString(code)}";

    /// <summary>扫码登录二维码内容：服务器地址、登录 ID 与一次性票据。</summary>
    public static string BuildLoginQrPayload(string serverUrl, string username, string ticket) =>
        $"{QrScheme}://login?server={Uri.EscapeDataString(serverUrl)}&user={Uri.EscapeDataString(username)}&ticket={Uri.EscapeDataString(ticket)}";
}
