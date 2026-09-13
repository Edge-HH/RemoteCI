using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>按数据库中的管理员设置定时请求插件重新生成七日课表。</summary>
public sealed class SchedulePullWorker(
    IServiceScopeFactory scopeFactory,
    ScheduleSyncService scheduleSync,
    TimeProvider timeProvider,
    ILogger<SchedulePullWorker> logger) : BackgroundService
{
    private readonly SchedulePullCadence _cadence = new(timeProvider.GetUtcNow());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckOnceAsync(timeProvider.GetUtcNow(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 主机关闭和调试停止都会取消等待；这是正常生命周期，不应作为用户未处理异常暴露给 VS。
        }
    }

    internal async Task CheckOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SchedulePullSettings>();
        var interval = await settings.GetIntervalAsync(ct);
        if (!_cadence.IsDue(interval, now)) return;

        _cadence.MarkAttempt(now);
        var status = await scheduleSync.StartAsync(ScheduleSyncSource.Automatic, ct);
        if (status.State == ScheduleSyncTaskState.Running)
            logger.LogInformation("已按 {Interval} 分钟周期请求插件刷新课表", (int)interval);
        else if (status.State == ScheduleSyncTaskState.Busy)
            logger.LogInformation("已到课表拉取周期，但已有课表任务正在执行，本次自动拉取已跳过");
        else
            logger.LogDebug("自动课表拉取未启动：{Message}", status.Message);
    }
}
