using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidaySettingsStoreTests
{
    [Fact]
    public async Task Defaults_EnabledWithDefaultSource()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.CreateClient().GetAsync("/api/health");
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        Assert.Equal(new HolidaySettings(true, null), await store.GetSettingsAsync());
    }

    [Fact]
    public async Task SettingsOverridesAndSnapshots_PersistAcrossRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        var now = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.FromHours(8));
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            await first.CreateClient().GetAsync("/api/health");
            using var scope = first.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            await store.SetSettingsAsync(false, " https://example.com/holiday/{year}.json ");
            await store.SetOverrideAsync(new DateOnly(2026, 10, 10), 5, null, now);
            await store.SetOverrideAsync(new DateOnly(2026, 9, 20), null, null, now);
            await store.SaveSnapshotAsync(2026, HolidayTestData.Json2026, "https://a/2026.json", now);
            await store.SaveSnapshotAsync(2026, HolidayTestData.Json2026, "https://b/2026.json", now.AddHours(1));
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        await second.CreateClient().GetAsync("/api/health");
        using var secondScope = second.Services.CreateScope();
        var restored = secondScope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        Assert.Equal(new HolidaySettings(false, "https://example.com/holiday/{year}.json"), await restored.GetSettingsAsync());
        Assert.Equal(
            [new HolidayOverride(new DateOnly(2026, 9, 20), null), new HolidayOverride(new DateOnly(2026, 10, 10), 5)],
            await restored.GetOverridesAsync());
        var snapshot = Assert.Single(await restored.GetSnapshotsAsync());
        Assert.Equal("https://b/2026.json", snapshot.SourceUrl);
        Assert.True(await restored.RemoveOverrideAsync(new DateOnly(2026, 10, 10)));
        Assert.False(await restored.RemoveOverrideAsync(new DateOnly(2026, 10, 10)));
    }

    [Theory]
    [InlineData("http://example.com/{year}.json")]
    [InlineData("https://example.com/2026.json")]
    [InlineData("not a url {year}")]
    public void NormalizeTemplate_RejectsInvalidTemplates(string template)
    {
        Assert.Throws<ArgumentException>(() => HolidaySettingsStore.NormalizeTemplate(template));
    }

    [Fact]
    public void NormalizeTemplate_TreatsBlankAsDefault()
    {
        Assert.Null(HolidaySettingsStore.NormalizeTemplate("   "));
    }

    [Fact]
    public async Task SetOverride_RejectsWeekendWeekday()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.CreateClient().GetAsync("/api/health");
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SetOverrideAsync(new DateOnly(2026, 10, 10), 6, null, DateTimeOffset.UnixEpoch));
    }
}
