using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>系统设置“维护 → 系统更新”中保存的更新渠道与自动检查/自动下载开关。</summary>
public sealed record UpdateSettingsValues(UpdateChannel Channel, bool AutoCheck, bool AutoDownload);

public sealed class UpdateSettings(AppDbContext db)
{
    public async Task<UpdateSettingsValues> GetAsync(CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SystemMetadata();
        return new UpdateSettingsValues(
            Enum.IsDefined(row.UpdateChannel) ? row.UpdateChannel : UpdateChannel.Stable,
            row.UpdateAutoCheck,
            row.UpdateAutoCheck && row.UpdateAutoDownload);
    }

    public async Task SetAsync(UpdateSettingsValues values, CancellationToken ct = default)
    {
        var row = await db.SystemMetadata.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            row = new SystemMetadata();
            db.SystemMetadata.Add(row);
        }
        row.UpdateChannel = Enum.IsDefined(values.Channel) ? values.Channel : UpdateChannel.Stable;
        row.UpdateAutoCheck = values.AutoCheck;
        // 自动下载依赖自动检查，单独开启没有意义。
        row.UpdateAutoDownload = values.AutoCheck && values.AutoDownload;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>已下载并校验、等待管理员确认安装的更新包。</summary>
public sealed record DownloadedUpdate(string Version, UpdateChannel Channel, PreparedUpdate Update);

/// <summary>最近一次检查更新的结果，只保存在内存中；服务端重启后由后台检查重新获取。</summary>
public sealed record UpdateStatusSnapshot(
    DateTimeOffset? CheckedAt,
    UpdateChannel Channel,
    ReleaseInfo? Latest,
    string? Error,
    DownloadedUpdate? Downloaded,
    bool Downloading);

/// <summary>
/// 检查更新与自动下载的共享状态：后台定期检查和页面上的手动检查都写入同一份状态，
/// 自动下载好的更新包供“下载并更新”直接安装，不再重复下载。
/// </summary>
public sealed class UpdateAutomation(
    UpdateService updates,
    IOptions<ServerOptions> serverOptions,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<UpdateAutomation> logger)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private UpdateStatusSnapshot _status = new(null, UpdateChannel.Stable, null, null, null, false);

    public UpdateStatusSnapshot Status
    {
        get { lock (_gate) return _status; }
    }

    public string DatabasePath =>
        Path.IsPathRooted(serverOptions.Value.DatabasePath)
            ? serverOptions.Value.DatabasePath
            : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);

    /// <summary>保存设置后让后台检查立即运行一次，而不是等到下一个周期。</summary>
    public void RequestCheck()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    internal Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);

    /// <summary>按渠道检查最新版本并记录结果；<paramref name="download"/> 为 true 且有可安装的新版本时预先下载。</summary>
    public async Task<ReleaseInfo?> CheckAsync(UpdateChannel channel, bool download, CancellationToken ct)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await updates.FetchLatestReleaseAsync(channel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Update(status => status with { CheckedAt = time.GetUtcNow(), Channel = channel, Error = ex.Message });
            throw;
        }
        Update(status => status with { CheckedAt = time.GetUtcNow(), Channel = channel, Latest = latest, Error = null });
        if (download && latest is not null) await DownloadIfNeededAsync(latest, channel, ct);
        return latest;
    }

    /// <summary>新版本可安装、当前平台有对应包且尚未下载过时才需要自动下载。</summary>
    internal static bool ShouldDownload(
        ReleaseInfo latest, ReleaseAsset? asset, string currentVersion, DownloadedUpdate? downloaded) =>
        asset is not null &&
        UpdateService.IsNewer(UpdateService.NormalizeVersion(latest.Tag), currentVersion) &&
        !(downloaded is not null &&
          string.Equals(downloaded.Version, UpdateService.NormalizeVersion(latest.Tag), StringComparison.OrdinalIgnoreCase) &&
          Directory.Exists(downloaded.Update.ExtractedDirectory));

    private async Task DownloadIfNeededAsync(ReleaseInfo latest, UpdateChannel channel, CancellationToken ct)
    {
        var asset = updates.SelectServerAsset(latest);
        var previous = Status.Downloaded;
        if (!ShouldDownload(latest, asset, updates.CurrentVersion, previous)) return;
        Update(status => status with { Downloading = true });
        try
        {
            var prepared = await updates.PrepareUpdateAsync(latest, asset!, DatabasePath, environment.ContentRootPath, ct);
            var version = UpdateService.NormalizeVersion(latest.Tag);
            // 渠道上出现更高版本时，旧的预下载包不再需要。
            if (previous is not null && !string.Equals(previous.Update.StagingDirectory, prepared.StagingDirectory, StringComparison.OrdinalIgnoreCase))
                UpdateService.TryDeleteStaging(previous.Update.StagingDirectory);
            Update(status => status with { Downloaded = new DownloadedUpdate(version, channel, prepared) });
            logger.LogInformation("Downloaded server update {Version}", version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Update(status => status with { Error = "自动下载失败：" + ex.Message });
            throw;
        }
        finally
        {
            Update(status => status with { Downloading = false });
        }
    }

    /// <summary>取出与指定版本一致、文件仍在的预下载包；取出后由调用方负责安装。</summary>
    public PreparedUpdate? TakeDownloaded(string version)
    {
        lock (_gate)
        {
            if (_status.Downloaded is not { } downloaded ||
                !string.Equals(downloaded.Version, version, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(downloaded.Update.ExtractedDirectory))
                return null;
            _status = _status with { Downloaded = null };
            return downloaded.Update;
        }
    }

    private void Update(Func<UpdateStatusSnapshot, UpdateStatusSnapshot> change)
    {
        lock (_gate) _status = change(_status);
    }
}

/// <summary>按保存的渠道定期检查服务端更新，开启自动下载时预先下载并校验新版本。</summary>
public sealed class UpdateCheckWorker(
    IServiceScopeFactory scopes,
    UpdateAutomation automation,
    IHostEnvironment environment,
    ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时先等一会儿，避免与数据库迁移、插件重连抢占网络；保存设置会提前唤醒。
        var wait = StartupDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await automation.WaitForRequestAsync(wait, stoppingToken);
                wait = Interval;
                if (!UpdateService.CanSelfUpdateNow(environment.IsDevelopment())) continue;
                using var scope = scopes.CreateScope();
                var settings = await scope.ServiceProvider.GetRequiredService<UpdateSettings>().GetAsync(stoppingToken);
                if (!settings.AutoCheck) continue;
                await automation.CheckAsync(settings.Channel, settings.AutoDownload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // GitHub 在部分网络下不可达，失败只记录，下个周期再试。
                logger.LogWarning(ex, "Automatic update check failed");
            }
        }
    }
}
