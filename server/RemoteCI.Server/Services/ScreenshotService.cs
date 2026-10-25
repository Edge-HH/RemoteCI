using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 屏幕截图仓库：截图 PNG 落盘到数据目录下的 screenshots，元数据写在同名 .json 中，服务端重启后仍可查看；
/// 过期截图由 <see cref="ScreenshotCleanupWorker"/> 删除。备份只打包数据库，不包含这里的文件。
/// 待上传记录只在内存中，绑定目标插件凭据与一次性上传凭据，命令结束后未上传即丢弃。
/// </summary>
public sealed class ScreenshotService
{
    /// <summary>每个班级最多保留的截图数量，超出时删除最早的。</summary>
    public const int MaxPerClass = 30;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonDefaults.Options) { WriteIndented = false };
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly ConcurrentDictionary<string, ScreenshotRecord> _records = new(StringComparer.Ordinal);
    private readonly ServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ScreenshotService> _logger;

    public ScreenshotService(
        IOptions<ServerOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<ScreenshotService> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        var databasePath = Path.IsPathRooted(_options.DatabasePath)
            ? _options.DatabasePath
            : Path.Combine(environment.ContentRootPath, _options.DatabasePath);
        Directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "screenshots");
        System.IO.Directory.CreateDirectory(Directory);
        LoadExisting();
    }

    public string Directory { get; }

    /// <summary>单张截图上限：配置值与协议上限取小。</summary>
    public int MaxBytes => Math.Clamp(_options.ScreenshotMaxBytes, 1024, ScreenshotRequest.MaxBytes);

    public TimeSpan Retention => _options.ScreenshotRetention > TimeSpan.Zero ? _options.ScreenshotRetention : TimeSpan.FromHours(24);

    /// <summary>已上传且未过期的截图。</summary>
    public ScreenshotRecord? Get(string? id) =>
        id is not null && _records.TryGetValue(id, out var record) && record.Uploaded && !IsExpired(record) &&
        File.Exists(ContentPath(id))
            ? record
            : null;

    /// <summary>待上传的截图（插件上传鉴权用）。</summary>
    public ScreenshotRecord? GetPending(string? id) =>
        id is not null && _records.TryGetValue(id, out var record) && !record.Uploaded &&
        record.CreatedAt + PendingLifetime > _time.GetUtcNow()
            ? record
            : null;

    public string ContentPath(string id) => Path.Combine(Directory, id + ".png");

    public IReadOnlyList<ScreenshotRecord> ListForClass(Guid classId) =>
        _records.Values.Where(x => x.ClassId == classId && x.Uploaded && !IsExpired(x))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();

    /// <summary>登记一条待上传截图；命令下发前调用，保证插件一截完图就能通过上传鉴权。</summary>
    public ScreenshotRecord CreatePending(Guid classId, Guid credentialId, string deviceName, UserProfile requester)
    {
        var now = _time.GetUtcNow();
        var record = new ScreenshotRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ClassId = classId,
            CredentialId = credentialId,
            DeviceName = deviceName,
            UploadKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            RequestedByUserId = requester.Id,
            RequestedByName = requester.DisplayName,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        };
        _records[record.Id] = record;
        return record;
    }

    /// <summary>上传鉴权：插件凭据必须是登记的目标设备，且上传凭据匹配。</summary>
    public static bool CanUpload(ScreenshotRecord record, AuthPrincipal principal, string? key)
    {
        if (!principal.IsPlugin || principal.PluginCredentialId != record.CredentialId || string.IsNullOrEmpty(key))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(record.UploadKey));
    }

    /// <summary>接收插件上传的 PNG：超过上限或不是 PNG 时拒绝；同一记录只接受一次上传。</summary>
    public async Task StoreAsync(ScreenshotRecord record, Stream body, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new FileTransferException(ApiErrorCodes.InvalidRequest, $"截图不能超过 {FileTransferService.FormatSize(MaxBytes)}",
                    StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }
        var content = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        if (content.Length < 33 || !content.Span[..8].SequenceEqual(PngSignature))
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, "截图必须是 PNG");

        lock (record.Sync)
        {
            if (record.Uploaded)
                throw new FileTransferException(ApiErrorCodes.InvalidRequest, "截图已上传", StatusCodes.Status409Conflict);
            record.Uploaded = true;
        }
        var temp = ContentPath(record.Id) + ".tmp";
        await File.WriteAllBytesAsync(temp, content.ToArray(), ct);
        File.Move(temp, ContentPath(record.Id), overwrite: true);
        // PNG 的 IHDR 紧跟签名：宽高是大端 32 位整数。
        record.Width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.Span[16..]);
        record.Height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.Span[20..]);
        record.Size = content.Length;
        record.CreatedAt = _time.GetUtcNow();
        record.ExpiresAt = record.CreatedAt + Retention;
        await SaveAsync(record);
        TrimClass(record.ClassId);
        _logger.LogInformation("已收到 {Device} 的屏幕截图（{Size} 字节，{Id}）", record.DeviceName, record.Size, record.Id);
    }

    public void Delete(string id)
    {
        _records.TryRemove(id, out _);
        TryDelete(ContentPath(id));
        TryDelete(MetadataPath(id));
    }

    /// <summary>删除过期截图与超时未上传的记录，返回删除数量。</summary>
    public int Cleanup()
    {
        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var record in _records.Values
                     .Where(x => x.Uploaded ? IsExpired(x) : x.CreatedAt + PendingLifetime <= now).ToList())
        {
            Delete(record.Id);
            if (record.Uploaded) removed++;
        }
        foreach (var temp in System.IO.Directory.EnumerateFiles(Directory, "*.tmp"))
        {
            if (File.GetLastWriteTimeUtc(temp) < now.UtcDateTime - TimeSpan.FromHours(1)) TryDelete(temp);
        }
        return removed;
    }

    public static ScreenshotView ToView(ScreenshotRecord record) => new(
        record.Id, record.ClassId, record.DeviceName, record.RequestedByName, record.Width, record.Height,
        record.Size, record.CreatedAt, record.ExpiresAt, $"/api/screenshots/{record.Id}");

    private void TrimClass(Guid classId)
    {
        foreach (var old in ListForClass(classId).Skip(MaxPerClass).ToList()) Delete(old.Id);
    }

    private bool IsExpired(ScreenshotRecord record) => record.ExpiresAt <= _time.GetUtcNow();

    private string MetadataPath(string id) => Path.Combine(Directory, id + ".json");

    private async Task SaveAsync(ScreenshotRecord record)
    {
        try
        {
            var temp = MetadataPath(record.Id) + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temp, MetadataPath(record.Id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "截图元数据写入失败：{Id}", record.Id);
        }
    }

    private void LoadExisting()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ScreenshotRecord>(File.ReadAllText(path), JsonOptions);
                if (record is null || !record.Uploaded || record.Id != Path.GetFileNameWithoutExtension(path)) continue;
                _records[record.Id] = record;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "跳过无法读取的截图元数据：{Path}", path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class ScreenshotRecord
{
    [JsonIgnore] public object Sync { get; } = new();
    public string Id { get; set; } = string.Empty;
    public Guid ClassId { get; set; }
    public Guid CredentialId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    [JsonIgnore] public string UploadKey { get; set; } = string.Empty;
    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public bool Uploaded { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record ScreenshotView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("classId")] Guid ClassId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("url")] string Url);

/// <summary>截图结果：成功时带截图记录，失败时带插件或服务端的命令回执。</summary>
public sealed record ScreenshotOutcome(ScreenshotRecord? Record, CommandResult Result);

/// <summary>
/// 发起一次截图：选定班级（或指定设备）的插件，登记待上传记录，下发 CaptureScreen 并等待回执；
/// 插件只有在上传成功后才回执成功，因此回执成功即可直接返回截图。
/// </summary>
public sealed class ScreenshotDispatcher(ScreenshotService screenshots, PeerRegistry peers, AppDbContext db)
{
    /// <summary>截屏、编码与上传都在插件端完成，比普通命令多留一些时间。</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    public async Task<ScreenshotOutcome> CaptureAsync(
        Guid classId, Guid? credentialId, UserProfile requestedBy, CancellationToken ct = default)
    {
        // 设备快照按接入时间排序：未指定设备时与普通命令一样取该班最早接入的教室电脑。
        var devices = peers.GetPluginDeviceSnapshots();
        var target = credentialId is { } id
            ? devices.FirstOrDefault(x => x.PluginCredentialId == id && x.ClassId == classId)
            : devices.FirstOrDefault(x => x.ClassId == classId && x.Assigned);
        if (target?.PluginCredentialId is not { } targetCredential)
            return Failed(CommandResultCodes.PluginOffline, "教室电脑未在线，无法截图");
        if (!target.EffectiveCapabilities.Contains(RemoteCiCapabilities.ScreenCapture, StringComparer.Ordinal))
            return Failed(CommandResultCodes.CapabilityUnsupported, "教室电脑上的 RemoteCI 插件版本过旧，请升级后再截图");

        var notify = await db.SystemMetadata.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => x.ScreenshotNotifyClassroom).SingleAsync(ct);
        var record = screenshots.CreatePending(classId, targetCredential, target.DisplayName, requestedBy);
        var result = await peers.SendCommandAndWaitToConnectionAsync(new CommandMessage
        {
            Command = CommandKind.CaptureScreen,
            ClassId = classId,
            Screenshot = new ScreenshotRequest { Id = record.Id, UploadKey = record.UploadKey, Notify = notify },
            RequestedBy = requestedBy,
        }, target.ConnectionId, CommandTimeout, ct);

        if (result.Success && screenshots.Get(record.Id) is { } uploaded)
            return new ScreenshotOutcome(uploaded, result);
        screenshots.Delete(record.Id);
        return new ScreenshotOutcome(null, result.Success
            ? CommandResult.Failure(CommandResultCodes.InternalError, "插件回执成功但服务端没有收到截图")
            : result);
    }

    private static ScreenshotOutcome Failed(string code, string message) =>
        new(null, CommandResult.Failure(code, message));
}

/// <summary>每 30 分钟清理过期截图。</summary>
public sealed class ScreenshotCleanupWorker(ScreenshotService screenshots, ILogger<ScreenshotCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                var removed = screenshots.Cleanup();
                if (removed > 0) logger.LogInformation("已清理 {Count} 张过期截图", removed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "清理截图失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
