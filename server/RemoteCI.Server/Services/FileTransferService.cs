using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 文件中转仓库：上传的文件落盘到数据目录下的 file-transfers，插件凭下载凭据分段拉取。
/// 元数据（含每台设备的接收状态）写在同名 .json 中，服务端重启后仍可继续下载与查询；
/// 文件在保留期后由 <see cref="FileTransferCleanupWorker"/> 删除。备份只打包数据库，不包含这里的文件。
/// </summary>
public sealed class FileTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonDefaults.Options) { WriteIndented = false };

    private readonly ConcurrentDictionary<string, FileTransferRecord> _records = new(StringComparer.Ordinal);
    private readonly ServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FileTransferService> _logger;

    public FileTransferService(
        IOptions<ServerOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<FileTransferService> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        var databasePath = Path.IsPathRooted(_options.DatabasePath)
            ? _options.DatabasePath
            : Path.Combine(environment.ContentRootPath, _options.DatabasePath);
        Directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "file-transfers");
        System.IO.Directory.CreateDirectory(Directory);
        LoadExisting();
    }

    public string Directory { get; }

    /// <summary>单个文件上限：配置值与协议上限取小。</summary>
    public long MaxFileBytes => Math.Clamp(_options.FileTransferMaxBytes, 1, FileDistributionRequest.MaxTransferBytes);

    public TimeSpan Retention => _options.FileTransferRetention > TimeSpan.Zero ? _options.FileTransferRetention : TimeSpan.FromHours(24);

    public FileTransferRecord? Get(string? id) =>
        id is not null && _records.TryGetValue(id, out var record) && !IsExpired(record) && File.Exists(ContentPath(id))
            ? record
            : null;

    public string ContentPath(string id) => Path.Combine(Directory, id + ".bin");

    /// <summary>
    /// 流式接收上传内容：边写盘边计算 SHA-256，超过上限立即中止并删除半成品。
    /// </summary>
    public async Task<FileTransferRecord> CreateAsync(
        Stream body, string? rawFileName, UserProfile owner, CancellationToken ct = default)
    {
        var fileName = FileDistributionRequest.SanitizeFileName(rawFileName)
            ?? throw new FileTransferException(ApiErrorCodes.InvalidRequest, "文件名无效");
        var quota = _options.FileTransferQuotaBytes;
        var used = _records.Values.Sum(x => x.Size);
        if (quota > 0 && used >= quota)
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, "服务端文件中转空间已满，请稍后再试或删除已完成的传输");

        var id = Guid.NewGuid().ToString("N");
        var partPath = Path.Combine(Directory, id + ".part");
        long size = 0;
        string sha256;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxFileBytes)
                        throw new FileTransferException(ApiErrorCodes.InvalidRequest, $"文件不能超过 {FormatSize(MaxFileBytes)}");
                    if (quota > 0 && used + size > quota)
                        throw new FileTransferException(ApiErrorCodes.InvalidRequest, "服务端文件中转空间不足");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw new FileTransferException(ApiErrorCodes.InvalidRequest, "文件内容为空");
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            File.Move(partPath, ContentPath(id));
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        var now = _time.GetUtcNow();
        var record = new FileTransferRecord
        {
            Id = id,
            FileName = fileName,
            Size = size,
            Sha256 = sha256,
            DownloadKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            OwnerUserId = owner.Id,
            OwnerName = owner.DisplayName,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        };
        _records[id] = record;
        await SaveAsync(record);
        _logger.LogInformation("文件中转已上传：{Name}（{Size} 字节，{Id}）", fileName, size, id);
        return record;
    }

    /// <summary>登记一台目标设备；命令下发前调用，保证插件一收到命令就能通过下载鉴权。</summary>
    public void AddDelivery(FileTransferRecord record, FileTransferDelivery delivery)
    {
        lock (record.Sync)
        {
            // 重新发送时替换同一设备的旧记录，以及同班此前“离线”等失败占位。
            record.Deliveries.RemoveAll(x => x.CredentialId == delivery.CredentialId ||
                (x.ClassId == delivery.ClassId && x.State == FileTransferState.Failed));
            record.Deliveries.Add(delivery);
        }
    }

    /// <summary>更新某台设备的接收状态；最终状态不会被迟到的进度覆盖。</summary>
    public async Task UpdateDeliveryAsync(
        FileTransferRecord record, Guid credentialId, Action<FileTransferDelivery> update)
    {
        bool persist;
        lock (record.Sync)
        {
            var delivery = record.Deliveries.FirstOrDefault(x => x.CredentialId == credentialId);
            if (delivery is null) return;
            var before = delivery.State;
            update(delivery);
            delivery.UpdatedAt = _time.GetUtcNow();
            persist = before != delivery.State;
        }
        if (persist) await SaveAsync(record);
    }

    /// <summary>插件上报的进度；只接受该插件凭据登记过的传输。</summary>
    public async Task<bool> ReportProgressAsync(Guid? credentialId, FileTransferProgress progress)
    {
        if (credentialId is not { } credential || Get(progress.TransferId) is not { } record) return false;
        if (!record.Deliveries.Any(x => x.CredentialId == credential)) return false;
        await UpdateDeliveryAsync(record, credential, delivery =>
        {
            if (delivery.State is FileTransferState.Completed or FileTransferState.Failed &&
                progress.State is not (FileTransferState.Completed or FileTransferState.Failed))
                return;
            delivery.State = progress.State is FileTransferState.Pending ? FileTransferState.Downloading : progress.State;
            delivery.ReceivedBytes = Math.Clamp(progress.ReceivedBytes, 0, record.Size);
            if (delivery.State == FileTransferState.Completed) delivery.ReceivedBytes = record.Size;
            delivery.Message = Clip(progress.Message, 300);
            delivery.SavedPath = Clip(progress.SavedPath, 400);
        });
        return true;
    }

    /// <summary>下载鉴权：插件凭据必须是该传输登记的目标设备，且下载凭据匹配。</summary>
    public bool CanDownload(FileTransferRecord record, AuthPrincipal principal, string? key)
    {
        if (!principal.IsPlugin || principal.PluginCredentialId is not { } credential || string.IsNullOrEmpty(key))
            return false;
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(record.DownloadKey)))
            return false;
        lock (record.Sync) return record.Deliveries.Any(x => x.CredentialId == credential);
    }

    /// <summary>上传者本人或系统管理员可以查看、继续分发与删除。</summary>
    public static bool CanManage(FileTransferRecord record, UserProfile user) =>
        user.Role == UserRole.Admin || record.OwnerUserId == user.Id;

    public void Delete(string id)
    {
        _records.TryRemove(id, out _);
        TryDelete(ContentPath(id));
        TryDelete(MetadataPath(id));
    }

    /// <summary>删除过期传输与遗留的上传半成品，返回删除数量。</summary>
    public int Cleanup()
    {
        var removed = 0;
        foreach (var record in _records.Values.Where(IsExpired).ToList())
        {
            Delete(record.Id);
            removed++;
        }
        var staleBefore = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(6);
        foreach (var part in System.IO.Directory.EnumerateFiles(Directory, "*.part"))
        {
            if (File.GetLastWriteTimeUtc(part) < staleBefore) TryDelete(part);
        }
        return removed;
    }

    public FileTransferView ToView(FileTransferRecord record)
    {
        lock (record.Sync)
        {
            return new FileTransferView(
                record.Id, record.FileName, record.Size, record.Sha256, record.OwnerName,
                record.CreatedAt, record.ExpiresAt,
                record.Deliveries.Select(x => new FileTransferDeliveryView(
                    x.ClassId, x.TargetName, x.State, x.ReceivedBytes, record.Size,
                    record.Size == 0 ? 0 : (int)Math.Floor(x.ReceivedBytes * 100d / record.Size),
                    x.Message, x.SavedPath, x.UpdatedAt)).ToList());
        }
    }

    private bool IsExpired(FileTransferRecord record) => record.ExpiresAt <= _time.GetUtcNow();

    private string MetadataPath(string id) => Path.Combine(Directory, id + ".json");

    private async Task SaveAsync(FileTransferRecord record)
    {
        string json;
        lock (record.Sync) json = JsonSerializer.Serialize(record, JsonOptions);
        try
        {
            var temp = MetadataPath(record.Id) + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, MetadataPath(record.Id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "文件中转元数据写入失败：{Id}", record.Id);
        }
    }

    private void LoadExisting()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<FileTransferRecord>(File.ReadAllText(path), JsonOptions);
                if (record is null || record.Id != Path.GetFileNameWithoutExtension(path)) continue;
                _records[record.Id] = record;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "跳过无法读取的文件中转元数据：{Path}", path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}

public sealed class FileTransferRecord
{
    [JsonIgnore] public object Sync { get; } = new();
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadKey { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public List<FileTransferDelivery> Deliveries { get; set; } = [];
}

/// <summary>一台目标设备（按插件凭据区分）的接收状态。</summary>
public sealed class FileTransferDelivery
{
    public Guid CredentialId { get; set; }
    public Guid ClassId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public FileTransferState State { get; set; }
    public long ReceivedBytes { get; set; }
    public string? Message { get; set; }
    public string? SavedPath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record FileTransferView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("ownerName")] string OwnerName,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("deliveries")] IReadOnlyList<FileTransferDeliveryView> Deliveries);

public sealed record FileTransferDeliveryView(
    [property: JsonPropertyName("classId")] Guid ClassId,
    [property: JsonPropertyName("targetName")] string TargetName,
    [property: JsonPropertyName("state")] FileTransferState State,
    [property: JsonPropertyName("receivedBytes")] long ReceivedBytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("percent")] int Percent,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("savedPath")] string? SavedPath,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed class FileTransferException(string code, string message, int status = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>每 30 分钟清理过期的中转文件。</summary>
public sealed class FileTransferCleanupWorker(FileTransferService transfers, ILogger<FileTransferCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                var removed = transfers.Cleanup();
                if (removed > 0) logger.LogInformation("已清理 {Count} 个过期的中转文件", removed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "清理中转文件失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
