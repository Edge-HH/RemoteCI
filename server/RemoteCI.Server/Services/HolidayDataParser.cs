using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RemoteCI.Server.Services;

/// <summary>holiday-cn 年度文件中的一天。</summary>
public sealed record HolidayEntry(DateOnly Date, string Name, bool IsOffDay);

/// <summary>
/// 解析 NateScarlet/holiday-cn 的年度 JSON。任何一个条目不合法都视为整份文件不可信并拒绝，
/// 避免把半截或被篡改的数据写进快照、进而错误地关闭或开启教室课表。
/// </summary>
public static class HolidayDataParser
{
    public const int MaxBytes = 256 * 1024;
    private const int MaxNameLength = 20;

    public static IReadOnlyList<HolidayEntry> Parse(int year, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException($"{year} 年节假日数据超过 256 KiB");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("days", out var days) ||
                days.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{year} 年节假日数据缺少 days 数组");
            return days.EnumerateArray().Select(item => ParseDay(year, item)).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{year} 年节假日数据不是有效的 JSON", ex);
        }
    }

    private static HolidayEntry ParseDay(int year, JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString()!.Trim() is { Length: > 0 and <= MaxNameLength } trimmed &&
            item.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
            Math.Abs(day.Year - year) <= 1 &&
            item.TryGetProperty("isOffDay", out var off) && off.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new HolidayEntry(day, trimmed, off.GetBoolean());
        var raw = item.GetRawText();
        throw new InvalidDataException($"{year} 年节假日数据包含无效条目：{raw[..Math.Min(raw.Length, 80)]}");
    }
}
