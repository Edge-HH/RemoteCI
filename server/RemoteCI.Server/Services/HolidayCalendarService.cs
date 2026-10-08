using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>调休功能使用的本地时钟；测试替换为固定时间，避免依赖运行测试当天的日期。</summary>
public class HolidayClock(TimeProvider time)
{
    public virtual DateTimeOffset Now => time.GetLocalNow();
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}

public sealed record HolidayRefreshStatus(DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastError);

public sealed record HolidayMakeupView(string Date, int? AutoWeekday, int? FollowWeekday, string FollowSource);

public sealed record HolidayPeriodView(string Name, string? OffStart, string? OffEnd, IReadOnlyList<HolidayMakeupView> MakeupDays);

public sealed record HolidayOverview(
    bool Enabled,
    string? SourceUrlTemplate,
    IReadOnlyList<string> DefaultSources,
    HolidayRefreshStatus Status,
    IReadOnlyList<HolidayPeriodView> Periods,
    IReadOnlyList<string> StaleOverrideDates);

/// <summary>调休操作的业务错误（例如给非调休上学日设置补课），由 API 映射为 400。</summary>
public sealed class HolidayOperationException(string message) : Exception(message);

/// <summary>拉取节假日数据、组装调休日历并推送给插件；管理入口（API/WebUI）也经由这里修改设置。</summary>
public sealed class HolidayCalendarService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    PeerRegistry peers,
    HolidayClock clock,
    ILogger<HolidayCalendarService> logger)
{
    public const string HttpClientName = "holiday-cn";

    public static IReadOnlyList<string> DefaultSources { get; } =
    [
        "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json",
        "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json",
    ];

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _publishLock = new();
    private string? _lastPublishedKey;
    private HolidayRefreshStatus _status = new(null, null, null);

    public HolidayRefreshStatus Status => Volatile.Read(ref _status);

    public async Task<HolidayRefreshStatus> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            var settings = await store.GetSettingsAsync(ct);
            IReadOnlyList<string> sources = settings.SourceUrlTemplate is { } custom ? [custom] : DefaultSources;
            var now = clock.Now;
            var errors = new List<string>();
            foreach (var year in new[] { now.Year, now.Year + 1 })
            {
                var outcome = await FetchYearAsync(year, sources, ct);
                if (outcome.Json is { } json)
                    await store.SaveSnapshotAsync(year, json, outcome.Url!, now, ct);
                // 次年数据通常 11 月后才发布，此前的 404 不算故障。
                else if (!(outcome.NotPublished && year > now.Year))
                    errors.Add(outcome.Error);
            }
            var status = errors.Count == 0
                ? new HolidayRefreshStatus(now, now, null)
                : new HolidayRefreshStatus(now, Status.LastSuccessAt, string.Join("；", errors));
            Volatile.Write(ref _status, status);
            if (status.LastError is not null)
                logger.LogWarning("节假日数据刷新失败，继续使用已有快照：{Error}", status.LastError);
        }
        finally
        {
            _refreshGate.Release();
        }
        await PublishAsync(ct: ct);
        return Status;
    }

    public async Task<HolidayCalendar> BuildCalendarAsync(CancellationToken ct = default)
    {
        var (settings, days, _) = await LoadAsync(ct);
        return HolidayCalendarBuilder.ToCalendar(days, settings.Enabled, clock.Today, clock.Now);
    }

    public async Task<HolidayOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var (settings, days, overrides) = await LoadAsync(ct);
        var today = clock.Today;
        var periods = days
            .GroupBy(x => (x.Year, x.Name))
            .Where(group => group.Max(x => x.Date) >= today)
            .OrderBy(group => group.Min(x => x.Date))
            .Select(group =>
            {
                var offs = group.Where(x => x.Kind == HolidayDayKinds.Off).Select(x => x.Date).ToList();
                return new HolidayPeriodView(
                    group.Key.Name,
                    offs.Count > 0 ? HolidayCalendarBuilder.Format(offs.Min()) : null,
                    offs.Count > 0 ? HolidayCalendarBuilder.Format(offs.Max()) : null,
                    group.Where(x => x.Kind == HolidayDayKinds.Makeup)
                        .Select(x => new HolidayMakeupView(HolidayCalendarBuilder.Format(x.Date), x.AutoWeekday, x.FollowWeekday, x.FollowSource!))
                        .ToList());
            })
            .ToList();
        var makeupDates = days.Where(x => x.Kind == HolidayDayKinds.Makeup).Select(x => x.Date).ToHashSet();
        var stale = overrides.Where(x => !makeupDates.Contains(x.Date)).Select(x => HolidayCalendarBuilder.Format(x.Date)).ToList();
        return new HolidayOverview(settings.Enabled, settings.SourceUrlTemplate, DefaultSources, Status, periods, stale);
    }

    public async Task<HolidayOverview> UpdateSettingsAsync(bool enabled, string? template, CancellationToken ct = default)
    {
        bool sourceChanged;
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            var before = await store.GetSettingsAsync(ct);
            await store.SetSettingsAsync(enabled, template, ct);
            sourceChanged = (await store.GetSettingsAsync(ct)).SourceUrlTemplate != before.SourceUrlTemplate;
        }
        // 换了数据源就立刻按新源拉一次（RefreshAsync 内部会发布），管理员保存后马上能看到结果。
        if (sourceChanged) await RefreshAsync(ct);
        else await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    public async Task<HolidayOverview> SetOverrideAsync(
        DateOnly date, int? followWeekday, Guid? userId, CancellationToken ct = default)
    {
        var (_, days, _) = await LoadAsync(ct);
        if (!days.Any(x => x.Kind == HolidayDayKinds.Makeup && x.Date == date))
            throw new HolidayOperationException($"{HolidayCalendarBuilder.Format(date)} 不是调休上学日");
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>()
                .SetOverrideAsync(date, followWeekday, userId, clock.Now, ct);
        await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    public async Task<HolidayOverview> RemoveOverrideAsync(DateOnly date, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>().RemoveOverrideAsync(date, ct);
        await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    /// <summary>日历内容（不含生成时间）变化时才广播，避免每次刷新都让插件重算。</summary>
    public async Task PublishAsync(bool force = false, CancellationToken ct = default)
    {
        var calendar = await BuildCalendarAsync(ct);
        var key = JsonSerializer.Serialize(new { calendar.Enabled, calendar.Days }, JsonDefaults.Options);
        lock (_publishLock)
        {
            if (!force && key == _lastPublishedKey) return;
            _lastPublishedKey = key;
        }
        await peers.BroadcastToPluginsWithCapabilityAsync(
            RemoteCiCapabilities.HolidayCalendar, Envelope.HolidayCalendar(calendar), ct);
    }

    public async Task SendToConnectionAsync(Guid connectionId, CancellationToken ct = default) =>
        await peers.SendToPluginConnectionAsync(connectionId, Envelope.HolidayCalendar(await BuildCalendarAsync(ct)), ct);

    private async Task<(HolidaySettings Settings, IReadOnlyList<ResolvedHolidayDay> Days, IReadOnlyList<HolidayOverride> Overrides)>
        LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
        var settings = await store.GetSettingsAsync(ct);
        var overrides = await store.GetOverridesAsync(ct);
        var years = new Dictionary<int, IReadOnlyList<HolidayEntry>>();
        foreach (var snapshot in await store.GetSnapshotsAsync(ct))
        {
            try { years[snapshot.Year] = HolidayDataParser.Parse(snapshot.Year, snapshot.RawJson); }
            catch (InvalidDataException ex) { logger.LogWarning(ex, "{Year} 年节假日快照无法解析，已忽略", snapshot.Year); }
        }
        return (settings, HolidayCalendarBuilder.Resolve(years, overrides), overrides);
    }

    private async Task<FetchOutcome> FetchYearAsync(int year, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var client = httpClients.CreateClient(HttpClientName);
        string? error = null;
        var notFound = 0;
        foreach (var template in sources)
        {
            var url = template.Replace("{year}", year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) { notFound++; continue; }
                if (!response.IsSuccessStatusCode)
                {
                    error = $"{year} 年：{url} 返回 {(int)response.StatusCode}";
                    continue;
                }
                var json = await ReadLimitedAsync(response.Content, ct);
                HolidayDataParser.Parse(year, json);
                return new FetchOutcome(json, url, false, string.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
                                       (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                error = $"{year} 年：{ex.Message}";
            }
        }
        // 镜像故障（如 jsDelivr 502）不代表文件不存在；只要有源明确返回 404，就视为该年数据尚未发布。
        return notFound > 0
            ? new FetchOutcome(null, null, true, $"{year} 年节假日数据尚未发布")
            : new FetchOutcome(null, null, false, error ?? $"{year} 年节假日数据获取失败");
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > HolidayDataParser.MaxBytes)
                throw new InvalidDataException("节假日数据超过 256 KiB");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record FetchOutcome(string? Json, string? Url, bool NotPublished, string Error);
}
