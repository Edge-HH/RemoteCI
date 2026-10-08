using System.Text.Json;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class HolidayCalendarStoreTests
{
    internal static HolidayCalendar National(int? followWeekday = 3) => new()
    {
        Enabled = true,
        Days =
        [
            new HolidayCalendarDay { Date = "2026-10-01", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            new HolidayCalendarDay { Date = "2026-10-07", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            new HolidayCalendarDay
            {
                Date = "2026-10-10", Kind = HolidayDayKinds.Makeup, Name = "国庆节",
                FollowWeekday = followWeekday, FollowSource = followWeekday is null ? HolidayFollowSources.Skip : HolidayFollowSources.Auto,
            },
        ],
    };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "HolidayCalendar.json");

    [Fact]
    public void Apply_PersistsAndReloads()
    {
        var path = TempPath();
        var store = new HolidayCalendarStore(path);
        var changed = 0;
        store.Changed += () => changed++;

        store.Apply(National());
        var reloaded = new HolidayCalendarStore(path);

        Assert.Equal(1, changed);
        Assert.Equal(HolidayDayKinds.Makeup, reloaded.Find(new DateTime(2026, 10, 10, 8, 0, 0))?.Kind);
        Assert.Null(reloaded.Find(new DateTime(2026, 10, 9)));
    }

    [Fact]
    public void DisabledCalendar_FindsNothing()
    {
        var store = new HolidayCalendarStore(TempPath());
        var calendar = National();
        calendar.Enabled = false;

        store.Apply(calendar);

        Assert.Null(store.Find(new DateTime(2026, 10, 1)));
    }

    [Fact]
    public void CorruptFile_StartsEmpty()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");

        Assert.Empty(new HolidayCalendarStore(path).Current.Days);
    }

    [Fact]
    public void Message_ReadsCalendarFromEnvelope()
    {
        var wire = JsonSerializer.Serialize(Envelope.HolidayCalendar(National()), JsonDefaults.Options);
        var envelope = JsonSerializer.Deserialize<Envelope>(wire, JsonDefaults.Options)!;

        Assert.True(HolidayCalendarMessage.TryRead(envelope, out var calendar));
        Assert.Equal(3, calendar.Days.Single(x => x.Kind == HolidayDayKinds.Makeup).FollowWeekday);
        Assert.False(HolidayCalendarMessage.TryRead(Envelope.AccountSync(new AccountSync()), out _));
    }
}
