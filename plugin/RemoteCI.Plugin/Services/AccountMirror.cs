using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 服务端授权镜像。只保存账号元数据和设备会话验证器，绝不接收或保存用户密码哈希。
/// 信任窗口说明：镜像通过云端连接更新，服务端撤销权限/禁用账号后，局域网端最长滞后
/// PrivilegedOfflineTtl（24 小时）才能感知，期间被撤销的账号在 LAN 上仍按旧镜像授权；
/// 该窗口之后有效权限统一收缩为 ViewCurrentCourse。这是局域网离线可用性的设计权衡。
/// </summary>
public sealed class AccountMirror
{
    public static readonly TimeSpan PrivilegedOfflineTtl = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly string _path;
    private AccountSync _sync;

    public AccountMirror(string path)
    {
        _path = path;
        _sync = Load(path);
        // v1 草稿曾写入密码哈希；启动时立即按 v2 DTO 原子重写，移除所有未知旧字段。
        if (File.Exists(path)) PersistLocked();
    }
    public long Version { get { lock (_gate) return _sync.Version; } }
    public string ServerVersion { get { lock (_gate) return _sync.ServerVersion; } }
    /// <summary>插件归属班级名称（服务端按班级生成授权镜像时下发）；旧版服务端为 null。</summary>
    public string? ClassName { get { lock (_gate) return _sync.ClassName; } }
    public IReadOnlyList<string> ServerCapabilities
    {
        get
        {
            lock (_gate)
                return (_sync.ServerCapabilities ?? RemoteCiCapabilities.Baseline.ToList()).ToArray();
        }
    }
    public DateTimeOffset GeneratedAt { get { lock (_gate) return _sync.GeneratedAt; } }
    public bool AllowsPrivilegedOperations
    {
        get { lock (_gate) return DateTimeOffset.UtcNow - _sync.GeneratedAt <= PrivilegedOfflineTtl; }
    }

    public bool TryVerify(
        Guid sessionId,
        AuthChallenge challenge,
        string clientNonce,
        string proof,
        out UserProfile? profile)
    {
        lock (_gate)
        {
            profile = null;
            var now = DateTimeOffset.UtcNow;
            // 复核挑战有效期：调用方已先检查，但验证器自身不能信任外部检查，
            // 防止未来新调用方遗漏后过期挑战仍可签名通过。
            if (challenge.ExpiresAt <= now) return false;
            var session = _sync.Sessions.FirstOrDefault(x => x.Id == sessionId && x.ExpiresAt > now);
            if (session is null) return false;
            var account = _sync.Accounts.FirstOrDefault(x => x.Id == session.UserId && x.Enabled);
            if (account is null) return false;

            byte[] verifier;
            byte[] supplied;
            try
            {
                verifier = Convert.FromHexString(session.Verifier);
                supplied = Convert.FromBase64String(proof);
            }
            catch (FormatException)
            {
                return false;
            }

            var canonical = CanonicalProof(challenge, sessionId, clientNonce);
            var expected = HMACSHA256.HashData(verifier, Encoding.UTF8.GetBytes(canonical));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return false;

            profile = account.ToProfile();
            if (now - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return true;
        }
    }

    public UserProfile? GetProfile(Guid id)
    {
        lock (_gate)
        {
            var account = _sync.Accounts.FirstOrDefault(x => x.Enabled && x.Id == id);
            if (account is null) return null;
            var profile = account.ToProfile();
            if (DateTimeOffset.UtcNow - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return profile;
        }
    }

    public UserProfile? GetProfileForSession(Guid userId, Guid sessionId)
    {
        lock (_gate)
        {
            if (!_sync.Sessions.Any(x => x.Id == sessionId && x.UserId == userId && x.ExpiresAt > DateTimeOffset.UtcNow))
                return null;
            var account = _sync.Accounts.FirstOrDefault(x => x.Enabled && x.Id == userId);
            if (account is null) return null;
            var profile = account.ToProfile();
            if (DateTimeOffset.UtcNow - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return profile;
        }
    }

    public void Apply(AccountSync sync)
    {
        lock (_gate)
        {
            if (!ShouldApplyLocked(sync)) return;
            _sync = sync;
            PersistLocked();
        }
        Changed?.Invoke();
    }

    private bool ShouldApplyLocked(AccountSync incoming)
    {
        if (incoming.Version > _sync.Version) return true;
        // 版本回退或重复：仅当服务端实例变化（如数据库重建后版本号重新计数）时强制覆盖，
        // 否则旧镜像会永久拒绝新同步，导致已禁用账号的旧会话持续可用。
        // 旧版服务端不携带实例标识（空 Guid），保持原单调比较行为。
        return incoming.ServerInstanceId != Guid.Empty
            && incoming.ServerInstanceId != _sync.ServerInstanceId;
    }

    public event Action? Changed;

    public static string CanonicalProof(AuthChallenge challenge, Guid sessionId, string clientNonce) =>
        $"{Protocol.Version}|{challenge.ChallengeId}|{challenge.Nonce}|{clientNonce}|{sessionId:N}";

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_sync, JsonDefaults.Options));
            File.Move(temporary, _path, true);
            // 镜像含设备验证器（LAN HMAC 密钥材料），限制为仅当前用户可读。
            FileProtection.RestrictToCurrentUser(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 镜像文件不可写时降级为纯内存态，不允许插件初始化或同步因此失败。
        }
    }

    private static AccountSync Load(string path)
    {
        if (!File.Exists(path)) return new AccountSync();
        try
        {
            return JsonSerializer.Deserialize<AccountSync>(File.ReadAllText(path), JsonDefaults.Options)
                ?? new AccountSync();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AccountSync();
        }
    }
}
