using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>插件托管的某日临时层。</summary>
public sealed class ManagedOverlay
{
    public Guid OverlayPlanId { get; set; }
    /// <summary>生成临时层所依据的基础课表（调休上学日为所跟星期那天的课表）。</summary>
    public Guid BasePlanId { get; set; }
    /// <summary>基础课表时间表的签名，变化时重建插件建立的临时层。</summary>
    public string? LayoutSignature { get; set; }
    /// <summary>调休上学日所跟的星期（1 为周一）；普通日为 null。</summary>
    public int? MakeupWeekday { get; set; }
    /// <summary>被临时层顶替的管理员预定课表，撤掉临时层时恢复。</summary>
    public Guid? DisplacedOrderedPlanId { get; set; }
    /// <summary>临时层由插件建立（撤销时删除）；false 表示接管的外来临时层（撤销时只恢复基础科目）。</summary>
    public bool Created { get; set; }
    /// <summary>上次写入的各节科目；与临时层实际科目不同说明有人在 ClassIsland 里改过。null 表示基线未知（刚接管）。</summary>
    public List<Guid>? Expected { get; set; }
}

/// <summary>托管临时层与调课的持久状态；落盘以便重启后继续托管、补报本地调课。</summary>
public sealed class ScheduleOverlayStateFile
{
    private readonly string _path;

    public ScheduleOverlayStateFile(string path, string? legacyHolidayStatePath = null)
    {
        _path = path;
        Document = Load(path) ?? ImportLegacy(legacyHolidayStatePath) ?? new StateDocument();
    }

    public StateDocument Document { get; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Document, JsonDefaults.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去时本次运行内仍按内存状态工作；重启后服务端会重新下发调课集合。
        }
    }

    private static StateDocument? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonDefaults.Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>升级：旧版调休建立的临时层（HolidayState.json）转为托管临时层，基线未知，接管时按差异保留其中的换课。</summary>
    private static StateDocument? ImportLegacy(string? legacyPath)
    {
        if (legacyPath is null) return null;
        try
        {
            if (!File.Exists(legacyPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (!json.RootElement.TryGetProperty("makeups", out var makeups) && !json.RootElement.TryGetProperty("Makeups", out makeups))
                return null;
            var document = new StateDocument();
            foreach (var entry in makeups.EnumerateObject())
            {
                var record = entry.Value;
                if (!TryGetGuid(record, "planId", out var planId)) continue;
                document.Dates[entry.Name] = new ManagedOverlay
                {
                    OverlayPlanId = planId,
                    Created = true,
                    MakeupWeekday = TryGetInt(record, "followWeekday"),
                };
            }
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryGetGuid(JsonElement element, string name, out Guid value)
    {
        value = default;
        return (element.TryGetProperty(name, out var property) || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out property)) &&
               property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out value);
    }

    private static int? TryGetInt(JsonElement element, string name) =>
        (element.TryGetProperty(name, out var property) || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out property)) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : null;

    public sealed class StateDocument
    {
        /// <summary>收到过服务端调课集合（服务端支持调课记录）。</summary>
        public bool ServerManaged { get; set; }
        public long ServerVersion { get; set; }
        public List<ScheduleSlotChange> ServerItems { get; set; } = [];
        /// <summary>本地产生、尚未被服务端确认的调课，按产生先后排列。</summary>
        public List<LocalScheduleAdjustment> PendingLocal { get; set; } = [];
        public Dictionary<string, ManagedOverlay> Dates { get; set; } = [];
    }
}
