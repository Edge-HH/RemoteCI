using Microsoft.Extensions.Options;

namespace RemoteCI.Server.Services;

/// <summary>低频复查在线连接的持久化授权状态，兜底处理外部数据库修改和主动失效遗漏。</summary>
public sealed class PeerAuthorizationRefreshWorker(
    PeerRegistry peers,
    TimeProvider timeProvider,
    IOptions<ServerOptions> options,
    ILogger<PeerAuthorizationRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ConnectionAuthorizationRefreshInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromMinutes(1);
            logger.LogWarning("连接授权复查周期必须大于零，已回退为 {Interval}", interval);
        }

        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await peers.RefreshAllAuthorizationsAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // 单次数据库或连接异常不能终止长期兜底任务，下个周期继续复查。
                    logger.LogError(ex, "在线连接授权兜底复查失败");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 主机关闭时正常结束。
        }
    }
}
