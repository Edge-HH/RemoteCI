using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>一条待投递的浏览器推送订阅（与 WebPushSubscription 实体解耦，便于在后台线程使用）。</summary>
public sealed record WebPushTarget(Guid Id, string Endpoint, string P256dh, string Auth);

/// <summary>
/// 浏览器 Web Push 发送器：RFC 8291（aes128gcm 载荷加密）+ RFC 8292（VAPID 身份）。
/// 只依赖 .NET 内置加密库；推送服务（FCM/Mozilla/WNS）由浏览器订阅时决定，服务端只需能访问其 HTTPS 端点。
/// </summary>
public sealed class WebPushSender(
    IHttpClientFactory httpClients,
    IServiceScopeFactory scopes,
    ILogger<WebPushSender> logger)
{
    public const string HttpClientName = "webpush";
    private const int RecordSize = 4096;
    private static readonly SemaphoreSlim KeyGate = new(1, 1);
    private VapidKeys? _keys;

    public sealed record VapidKeys(string PublicKey, string PrivateKey);

    /// <summary>读取或首次生成 VAPID 密钥对；密钥存库以便随配置备份，更换后旧订阅需要重新开启通知。</summary>
    public async Task<VapidKeys> GetKeysAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (_keys is { } cached) return cached;
        await KeyGate.WaitAsync(ct);
        try
        {
            if (_keys is { } again) return again;
            var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
            if (string.IsNullOrEmpty(metadata.VapidPublicKey) || string.IsNullOrEmpty(metadata.VapidPrivateKey))
            {
                var generated = GenerateKeys();
                metadata.VapidPublicKey = generated.PublicKey;
                metadata.VapidPrivateKey = generated.PrivateKey;
                await db.SaveChangesAsync(ct);
            }
            return _keys = new VapidKeys(metadata.VapidPublicKey!, metadata.VapidPrivateKey!);
        }
        finally
        {
            KeyGate.Release();
        }
    }

    public static VapidKeys GenerateKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        return new VapidKeys(Base64Url(UncompressedPoint(parameters.Q)), Base64Url(parameters.D!));
    }

    /// <summary>后台投递：不阻塞调用方；订阅已失效（404/410）时删除。</summary>
    public void SendInBackground(VapidKeys keys, IReadOnlyList<WebPushTarget> targets, object payload)
    {
        if (targets.Count == 0) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, RemoteCI.Shared.JsonDefaults.Options);
        _ = Task.Run(async () =>
        {
            var expired = new List<Guid>();
            foreach (var target in targets)
            {
                try
                {
                    var status = await SendAsync(keys, target, json, CancellationToken.None);
                    if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) expired.Add(target.Id);
                    else if ((int)status >= 400)
                        logger.LogWarning("Web Push 投递失败：{Status} {Host}", (int)status, SafeHost(target.Endpoint));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Web Push 投递异常：{Host}", SafeHost(target.Endpoint));
                }
            }
            if (expired.Count == 0) return;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.WebPushSubscriptions.Where(x => expired.Contains(x.Id)).ExecuteDeleteAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "清理失效的 Web Push 订阅失败");
            }
        });
    }

    public async Task<HttpStatusCode> SendAsync(VapidKeys keys, WebPushTarget target, byte[] payload, CancellationToken ct)
    {
        var endpoint = new Uri(target.Endpoint);
        var body = Encrypt(Base64UrlDecode(target.P256dh), Base64UrlDecode(target.Auth), payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        var jwt = CreateVapidJwt(keys, $"{endpoint.Scheme}://{endpoint.Authority}", DateTimeOffset.UtcNow.AddHours(12));
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={keys.PublicKey}");
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
        return response.StatusCode;
    }

    /// <summary>RFC 8291 aes128gcm：单记录加密，头部携带 salt、记录大小与服务端临时公钥。</summary>
    public static byte[] Encrypt(byte[] userAgentPublicKey, byte[] authSecret, byte[] plaintext)
    {
        if (plaintext.Length > RecordSize - 17 - 86)
            throw new ArgumentException("推送载荷过大", nameof(plaintext));
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverPublic = UncompressedPoint(ephemeral.ExportParameters(false).Q);
        using var userAgent = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] },
        });
        var sharedSecret = ephemeral.DeriveRawSecretAgreement(userAgent.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(16);
        var (key, nonce) = DeriveContentKeys(sharedSecret, authSecret, userAgentPublicKey, serverPublic, salt);

        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02; // 最后一条记录的分隔符
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, padded, cipher, tag);

        var result = new byte[16 + 4 + 1 + serverPublic.Length + cipher.Length + tag.Length];
        salt.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), RecordSize);
        result[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(result, 21);
        cipher.CopyTo(result, 21 + serverPublic.Length);
        tag.CopyTo(result, 21 + serverPublic.Length + cipher.Length);
        return result;
    }

    /// <summary>RFC 8291 §3.3/§3.4 密钥派生，加密与（测试中的）解密共用。</summary>
    public static (byte[] Key, byte[] Nonce) DeriveContentKeys(
        byte[] sharedSecret, byte[] authSecret, byte[] userAgentPublic, byte[] serverPublic, byte[] salt)
    {
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), userAgentPublic, serverPublic);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, authSecret), 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        return (key, nonce);
    }

    /// <summary>RFC 8292 VAPID：ES256 签名的 JWT，aud 为推送服务源。</summary>
    public static string CreateVapidJwt(VapidKeys keys, string audience, DateTimeOffset expiresAt)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["sub"] = "mailto:remoteci@localhost",
        }));
        var publicKey = Base64UrlDecode(keys.PublicKey);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64UrlDecode(keys.PrivateKey),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='));
    }

    private static byte[] UncompressedPoint(ECPoint point) => Concat([0x04], point.X!, point.Y!);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(x => x.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static string SafeHost(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
}
