namespace RemoteCI.Server.Services;

/// <summary>每 10 分钟把过期的待审批换课申请标记为过期，并清理已过日期的临时任课老师覆盖。</summary>
public sealed class ScheduleSwapSweepWorker(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<ScheduleSwapSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ScheduleSwapService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "清理过期换课申请失败");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
