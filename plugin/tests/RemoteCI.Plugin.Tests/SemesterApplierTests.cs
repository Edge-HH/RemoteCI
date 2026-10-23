using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SemesterApplierTests
{
    /// <summary>与 ClassIsland Settings 同名同类型的成员，验证反射访问路径。</summary>
    private sealed class FakeSettings
    {
        public DateTime SingleWeekStartTime { get; set; } = new(2025, 9, 1);
        public ObservableCollection<int> MultiWeekRotationOffset { get; set; } = [-1, -1, 1, 0, 0];
    }

    private sealed class FakeSettingsService
    {
        public FakeSettings Settings { get; } = new();
        public List<string> SavedNotes { get; } = [];
        public void SaveSettings(string note = "") => SavedNotes.Add(note);
    }

    private static SemesterApplier Applier(ISemesterHostOperations host) => new(host, NullLogger<SemesterApplier>.Instance);

    [Fact]
    public void Apply_WritesWeekOneMonday_ClearsOffsets_AndSkipsWhenAlreadyInSync()
    {
        var service = new FakeSettingsService();
        var host = new ClassIslandSemesterHost(() => service);
        Assert.True(host.HasRotationOffsets);

        Assert.True(Applier(host).Apply(new SemesterSync { WeekOneMonday = "2026-08-31" }));
        Assert.Equal(new DateTime(2026, 8, 31), service.Settings.SingleWeekStartTime);
        Assert.Empty(service.Settings.MultiWeekRotationOffset);
        Assert.Single(service.SavedNotes);

        Assert.False(Applier(host).Apply(new SemesterSync { WeekOneMonday = "2026-08-31" }));
        Assert.Single(service.SavedNotes);
    }

    [Fact]
    public void Apply_IgnoresInvalidDates_AndUnsupportedHosts()
    {
        var service = new FakeSettingsService();
        Assert.False(Applier(new ClassIslandSemesterHost(() => service)).Apply(new SemesterSync { WeekOneMonday = "2026/08/31" }));
        Assert.Empty(service.SavedNotes);
        var unsupported = new ClassIslandSemesterHost(() => new object());
        Assert.Null(unsupported.SingleWeekStartTime);
        Assert.False(Applier(unsupported).Apply(new SemesterSync { WeekOneMonday = "2026-08-31" }));
    }

    [Fact]
    public void SemesterMessage_ReadsOnlySemesterEnvelopes()
    {
        var wire = JsonSerializer.Serialize(Envelope.Semester(new SemesterSync { WeekOneMonday = "2026-08-31" }), JsonDefaults.Options);
        var envelope = JsonSerializer.Deserialize<Envelope>(wire, JsonDefaults.Options)!;

        Assert.True(SemesterMessage.TryRead(envelope, out var sync));
        Assert.Equal("2026-08-31", sync.WeekOneMonday);
        Assert.False(SemesterMessage.TryRead(Envelope.AccountSync(new AccountSync()), out _));
        Assert.Contains(RemoteCiCapabilities.Semester, RemoteCiCapabilities.Current);
        Assert.DoesNotContain(RemoteCiCapabilities.Semester, RemoteCiCapabilities.Baseline);
    }
}
