using Microsoft.Extensions.Options;

namespace RemoteCI.Server.Services;

/// <summary>启动后立即刷新节假日数据，之后每 12 小时刷新；跨天后（00:05 起）再发布一次，让 60 天窗口向前滚动。</summary>
public sealed class HolidayCalendarWorker(
    HolidayCalendarService holidays,
    HolidayClock clock,
    IOptions<ServerOptions> options,
    ILogger<HolidayCalendarWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan DailyPublishAfter = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HolidayAutoRefresh) return;
        DateTimeOffset? lastRefresh = null;
        var lastDate = clock.Today;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var now = clock.Now;
                if (lastRefresh is null || now - lastRefresh >= RefreshInterval)
                {
                    await holidays.RefreshAsync(stoppingToken);
                    lastRefresh = now;
                    lastDate = clock.Today;
                }
                else if (clock.Today != lastDate && now.TimeOfDay >= DailyPublishAfter)
                {
                    await holidays.PublishAsync(ct: stoppingToken);
                    lastDate = clock.Today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "调休日历后台任务失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
