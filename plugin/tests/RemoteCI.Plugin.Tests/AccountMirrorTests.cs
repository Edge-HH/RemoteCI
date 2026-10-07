using System.Security.Cryptography;
using System.Text;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class AccountMirrorTests
{
    [Fact]
    public void ValidDeviceProofAuthenticatesWithoutPasswordMaterial()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");
        var mirror = new AccountMirror(path);
        mirror.Apply(CreateSync(userId, sessionId, Convert.ToHexString(verifier).ToLowerInvariant(), DateTimeOffset.UtcNow));
        var challenge = new AuthChallenge
        {
            ChallengeId = Guid.NewGuid().ToString("N"),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        const string clientNonce = "client-nonce";
        var proof = Convert.ToBase64String(HMACSHA256.HashData(
            verifier,
            Encoding.UTF8.GetBytes(AccountMirror.CanonicalProof(challenge, sessionId, clientNonce))));

        Assert.True(mirror.TryVerify(sessionId, challenge, clientNonce, proof, out var profile));
        Assert.Equal(UserPermissions.All, profile?.Permissions);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void MirrorOlderThanTwentyFourHoursAuthenticatesAsReadOnly()
    {
        var secret = "device-secret";
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");
        var mirror = new AccountMirror(path);
        mirror.Apply(CreateSync(userId, sessionId, Convert.ToHexString(verifier).ToLowerInvariant(), DateTimeOffset.UtcNow.AddHours(-25)));
        var challenge = new AuthChallenge
        {
            ChallengeId = "expired-mirror",
            Nonce = "nonce",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        const string clientNonce = "client";
        var proof = Convert.ToBase64String(HMACSHA256.HashData(
            verifier,
            Encoding.UTF8.GetBytes(AccountMirror.CanonicalProof(challenge, sessionId, clientNonce))));

        Assert.True(mirror.TryVerify(sessionId, challenge, clientNonce, proof, out var profile));
        Assert.Equal(UserPermissions.ViewCurrentCourse, profile?.Permissions);
        Assert.False(mirror.AllowsPrivilegedOperations);
    }

    [Fact]
    public void ServerVersionSurvivesMirrorPersistenceForLanAuthentication()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");
        var mirror = new AccountMirror(path);
        mirror.Apply(CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow, "0.3.1"));

        var reloaded = new AccountMirror(path);

        Assert.Equal("0.3.1", reloaded.ServerVersion);
    }

    [Fact]
    public void ServerCapabilitiesPersistAndLegacySyncFallsBackToBaseline()
    {
        var path = TempMirrorPath();
        var sync = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        sync.ServerCapabilities = [RemoteCiCapabilities.ScheduleRead];
        var mirror = new AccountMirror(path);
        mirror.Apply(sync);

        Assert.Equal([RemoteCiCapabilities.ScheduleRead], new AccountMirror(path).ServerCapabilities);
        Assert.Equal(RemoteCiCapabilities.Baseline, new AccountMirror(TempMirrorPath()).ServerCapabilities);
    }

    [Fact]
    public void VersionRollbackFromNewServerInstanceForceOverwritesMirror()
    {
        var path = TempMirrorPath();
        var mirror = new AccountMirror(path);
        var oldInstanceId = Guid.NewGuid();
        var first = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        first.Version = 5;
        first.ServerInstanceId = oldInstanceId;
        mirror.Apply(first);

        // 数据库重建后版本号回退，但实例标识变化，应强制覆盖。
        var rebuilt = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        rebuilt.Version = 1;
        rebuilt.ServerInstanceId = Guid.NewGuid();
        mirror.Apply(rebuilt);

        Assert.Equal(1, mirror.Version);
    }

    [Fact]
    public void VersionRollbackFromSameServerInstanceIsRejected()
    {
        var mirror = new AccountMirror(TempMirrorPath());
        var instanceId = Guid.NewGuid();
        var first = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        first.Version = 5;
        first.ServerInstanceId = instanceId;
        mirror.Apply(first);

        var stale = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        stale.Version = 3;
        stale.ServerInstanceId = instanceId;
        mirror.Apply(stale);

        Assert.Equal(5, mirror.Version);
    }

    [Fact]
    public void LegacySyncWithoutInstanceIdKeepsMonotonicBehavior()
    {
        var mirror = new AccountMirror(TempMirrorPath());
        var first = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        first.Version = 5;
        mirror.Apply(first);

        // 旧版服务端不携带实例标识，版本回退仍按原逻辑拒绝。
        var stale = CreateSync(Guid.NewGuid(), Guid.NewGuid(), "00", DateTimeOffset.UtcNow);
        stale.Version = 1;
        mirror.Apply(stale);

        Assert.Equal(5, mirror.Version);
    }

    private static string TempMirrorPath() =>
        Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");

    private static AccountSync CreateSync(
        Guid userId,
        Guid sessionId,
        string verifier,
        DateTimeOffset generatedAt,
        string serverVersion = "0.3.1") => new()
    {
        Version = 1,
        ServerVersion = serverVersion,
        GeneratedAt = generatedAt,
        Accounts = [new SyncedAccount
        {
            Id = userId,
            Username = "admin",
            DisplayName = "管理员",
            Role = UserRole.Admin,
            EffectivePermissions = UserPermissions.All,
            Enabled = true,
            Version = 1,
        }],
        Sessions = [new SyncedDeviceSession
        {
            Id = sessionId,
            UserId = userId,
            Verifier = verifier,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        }],
    };
}
