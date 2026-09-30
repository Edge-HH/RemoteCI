using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class SchedulePullSettingsTests
{
    [Fact]
    public async Task SelectedInterval_PersistsAcrossServerRestart()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            await first.CreateClient().GetAsync("/api/health");
            using var scope = first.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SchedulePullSettings>();
            await settings.SetIntervalAsync(SchedulePullInterval.Hourly);
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        await second.CreateClient().GetAsync("/api/health");
        using var secondScope = second.Services.CreateScope();
        var restored = secondScope.ServiceProvider.GetRequiredService<SchedulePullSettings>();

        Assert.Equal(SchedulePullInterval.Hourly, await restored.GetIntervalAsync());
    }

    [Fact]
    public void Cadence_UsesConfiguredIntervalAndDisabledNeverBecomesDue()
    {
        var startedAt = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var cadence = new SchedulePullCadence(startedAt);

        Assert.False(cadence.IsDue(SchedulePullInterval.Disabled, startedAt.AddDays(2)));
        Assert.False(cadence.IsDue(SchedulePullInterval.Hourly, startedAt.AddMinutes(59)));
        Assert.True(cadence.IsDue(SchedulePullInterval.Hourly, startedAt.AddHours(1)));

        cadence.MarkAttempt(startedAt.AddHours(1));
        Assert.False(cadence.IsDue(SchedulePullInterval.Hourly, startedAt.AddHours(1).AddMinutes(59)));
        Assert.True(cadence.IsDue(SchedulePullInterval.Hourly, startedAt.AddHours(2)));
    }
}
