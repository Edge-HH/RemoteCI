using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed class AutomaticBackupWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<AutomaticBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await db.BackupConfigurations.SingleAsync(x => x.Id == 1, stoppingToken);
                if (!settings.Enabled) continue;
                var now = time.GetLocalNow();
                var slot = CurrentSlot(settings, now);
                if (slot is null || settings.LastScheduledAt >= slot) continue;
                settings.LastScheduledAt = slot;
                try { await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CreateLocalBackupAsync("automatic", stoppingToken); settings.LastSucceededAt=now; settings.LastError=null; }
                catch(Exception ex) { settings.LastError=ex.Message[..Math.Min(ex.Message.Length,1000)]; logger.LogError(ex,"Automatic backup failed"); }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal static DateTimeOffset? CurrentSlot(BackupConfiguration c, DateTimeOffset now)
    {
        var local=new DateTimeOffset(now.Date+c.TimeOfDay, now.Offset);
        if(c.Cadence==BackupCadence.Hourly) local=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,now.Offset);
        else if(c.Cadence==BackupCadence.Weekly) local=local.AddDays(-(((int)local.DayOfWeek-(int)c.DayOfWeek+7)%7));
        if(local>now) local=c.Cadence switch { BackupCadence.Hourly=>local.AddHours(-1), BackupCadence.Daily=>local.AddDays(-1), _=>local.AddDays(-7) };
        return local;
    }
}
