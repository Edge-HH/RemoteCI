using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

public interface IHolidayCalendarLookup
{
    HolidayCalendarDay? Find(DateTime date);
}

/// <summary>服务端最近一次下发的调休日历；落盘缓存，断网或服务端离线时继续按它生效。</summary>
public sealed class HolidayCalendarStore : IHolidayCalendarLookup
{
    private readonly string _path;
    private readonly object _lock = new();
    private HolidayCalendar _current;
    private Dictionary<string, HolidayCalendarDay> _index;

    public HolidayCalendarStore(string path)
    {
        _path = path;
        _current = Load(path);
        _index = BuildIndex(_current);
    }

    public event Action? Changed;

    public HolidayCalendar Current
    {
        get { lock (_lock) return _current; }
    }

    public HolidayCalendarDay? Find(DateTime date)
    {
        lock (_lock) return _index.GetValueOrDefault(date.ToString("yyyy-MM-dd"));
    }

    public void Apply(HolidayCalendar calendar)
    {
        lock (_lock)
        {
            _current = calendar;
            _index = BuildIndex(calendar);
            Persist(calendar);
        }
        Changed?.Invoke();
    }

    private static Dictionary<string, HolidayCalendarDay> BuildIndex(HolidayCalendar calendar) =>
        calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First())
            : [];

    private void Persist(HolidayCalendar calendar)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(calendar, JsonDefaults.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去时只在本次运行内生效，不能因此影响插件其他功能。
        }
    }

    private static HolidayCalendar Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HolidayCalendar>(File.ReadAllText(path), JsonDefaults.Options) ?? new HolidayCalendar()
                : new HolidayCalendar();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new HolidayCalendar();
        }
    }
}

/// <summary>从信封中取出调休日历；单独成类以便不经网络直接测试。</summary>
public static class HolidayCalendarMessage
{
    public static bool TryRead(Envelope envelope, out HolidayCalendar calendar)
    {
        calendar = null!;
        if (envelope.Type != Protocol.MessageTypeHolidayCalendar || envelope.Payload is null) return false;
        try
        {
            calendar = JsonSerializer.Deserialize<HolidayCalendar>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return calendar is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
