using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteCI.Server.Services;

/// <summary>网页扫码登录挑战的状态：等待扫码 → 已扫码待确认 → 已确认（或已拒绝）。</summary>
public enum WebQrLoginState
{
    Pending,
    Scanned,
    Approved,
    Denied,
    Expired,
}

/// <summary>手机扫码后看到的浏览器信息，用于确认是不是自己正在操作的电脑。</summary>
public sealed record WebQrLoginRequestInfo(string Browser, string? IpAddress, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>浏览器轮询看到的状态；ScannedBy 为扫码账号的显示名。</summary>
public sealed record WebQrLoginStatus(WebQrLoginState State, string? ScannedBy);

/// <summary>
/// WebUI 登录页的“手机扫码登录”：浏览器生成挑战并显示二维码（只含挑战码），
/// 已登录的手机 App 扫码、确认后，持有轮询令牌的那个浏览器才能换取网页会话。
/// 二维码被旁人拍下也无法冒用：没有轮询令牌就不能完成登录。挑战只在内存中保存 3 分钟、只能兑换一次。
/// </summary>
public sealed class WebQrLoginService(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    private const int MaxPending = 2000;

    private sealed class Challenge
    {
        public required string PollToken { get; init; }
        public required WebQrLoginRequestInfo Info { get; init; }
        public WebQrLoginState State { get; set; } = WebQrLoginState.Pending;
        public Guid? UserId { get; set; }
        public string? DisplayName { get; set; }
    }

    private readonly ConcurrentDictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);

    /// <summary>为浏览器创建挑战：返回写入二维码的挑战码与只保存在页面里的轮询令牌。</summary>
    public (string Code, string PollToken, DateTimeOffset ExpiresAt) Create(string? userAgent, string? ipAddress)
    {
        Sweep();
        if (_challenges.Count >= MaxPending)
            throw new InvalidOperationException("扫码登录请求过多，请稍后再试");
        var now = clock.GetUtcNow();
        var code = Secret(18);
        var poll = Secret(24);
        _challenges[code] = new Challenge
        {
            PollToken = poll,
            Info = new WebQrLoginRequestInfo(DescribeBrowser(userAgent), ipAddress, now, now + Lifetime),
        };
        return (code, poll, now + Lifetime);
    }

    /// <summary>手机扫码：把挑战标记为该账号已扫码，返回浏览器信息供确认；挑战无效或已被其他账号扫过时返回 null。</summary>
    public WebQrLoginRequestInfo? Scan(string code, Guid userId, string displayName)
    {
        if (!TryGetLive(code, out var challenge)) return null;
        lock (challenge)
        {
            if (challenge.State == WebQrLoginState.Pending)
            {
                challenge.State = WebQrLoginState.Scanned;
                challenge.UserId = userId;
                challenge.DisplayName = displayName;
                return challenge.Info;
            }
            return challenge.State == WebQrLoginState.Scanned && challenge.UserId == userId ? challenge.Info : null;
        }
    }

    /// <summary>扫码的同一账号确认或拒绝；返回是否生效。</summary>
    public bool Decide(string code, Guid userId, bool approve)
    {
        if (!TryGetLive(code, out var challenge)) return false;
        lock (challenge)
        {
            if (challenge.State != WebQrLoginState.Scanned || challenge.UserId != userId) return false;
            challenge.State = approve ? WebQrLoginState.Approved : WebQrLoginState.Denied;
            return true;
        }
    }

    /// <summary>浏览器轮询：轮询令牌不匹配或挑战不存在时按已过期处理。</summary>
    public WebQrLoginStatus Poll(string code, string pollToken)
    {
        if (!TryGetLive(code, out var challenge) || !TokenEquals(challenge.PollToken, pollToken))
            return new WebQrLoginStatus(WebQrLoginState.Expired, null);
        lock (challenge)
            return new WebQrLoginStatus(challenge.State, challenge.State == WebQrLoginState.Pending ? null : challenge.DisplayName);
    }

    /// <summary>浏览器兑换已确认的挑战：成功返回确认账号的 Id，挑战随即作废。</summary>
    public Guid? Consume(string code, string pollToken)
    {
        if (!TryGetLive(code, out var challenge) || !TokenEquals(challenge.PollToken, pollToken)) return null;
        lock (challenge)
        {
            if (challenge.State != WebQrLoginState.Approved || challenge.UserId is not { } userId) return null;
            _challenges.TryRemove(code, out _);
            return userId;
        }
    }

    private bool TryGetLive(string? code, out Challenge challenge)
    {
        challenge = null!;
        if (string.IsNullOrWhiteSpace(code) || !_challenges.TryGetValue(code, out var found)) return false;
        if (found.Info.ExpiresAt <= clock.GetUtcNow())
        {
            _challenges.TryRemove(code, out _);
            return false;
        }
        challenge = found;
        return true;
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (code, challenge) in _challenges)
            if (challenge.Info.ExpiresAt <= now) _challenges.TryRemove(code, out _);
    }

    private static bool TokenEquals(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));

    private static string Secret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>把 User-Agent 归纳成“系统 · 浏览器”，方便在手机上辨认。</summary>
    public static string DescribeBrowser(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "未知浏览器";
        var ua = userAgent;
        var os = ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "未知系统";
        var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "浏览器";
        return $"{os} · {browser}";
    }
}
