using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>学期同步所需的宿主能力：ClassIsland 的单周起始日期与多周轮换偏移（防腐层，便于单元测试）。</summary>
public interface ISemesterHostOperations
{
    /// <summary>当前单周起始日期；宿主不支持时为 null。</summary>
    DateTime? SingleWeekStartTime { get; }
    /// <summary>本机是否手动调整过多周轮换偏移（ClassIsland“调整轮换”）。</summary>
    bool HasRotationOffsets { get; }
    /// <summary>写入单周起始日期、清除轮换偏移并保存设置；宿主不支持时返回 false。</summary>
    bool Apply(DateTime weekOneMonday);
}

/// <summary>
/// 把服务端统一设置的学期开始日期写入 ClassIsland，使全校教室按同一教学周计算单双周与多周轮换，
/// 与服务端按档案推算的课表、教师自定义课程的单双周一致。
/// </summary>
public sealed class SemesterApplier(ISemesterHostOperations host, ILogger<SemesterApplier> logger)
{
    /// <summary>返回是否修改了宿主设置；已一致时不写入，避免每次重连都保存设置。</summary>
    public bool Apply(SemesterSync sync)
    {
        if (!DateTime.TryParseExact(sync.WeekOneMonday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday))
        {
            logger.LogWarning("服务端下发的学期开始日期无效：{Value}", sync.WeekOneMonday);
            return false;
        }
        if (host.SingleWeekStartTime?.Date == monday.Date && !host.HasRotationOffsets) return false;
        if (!host.Apply(monday))
        {
            logger.LogWarning("当前 ClassIsland 版本不支持设置单周起始日期，学期设置未同步");
            return false;
        }
        logger.LogInformation("已按服务端学期设置把单周起始日期设为 {Date:yyyy-MM-dd}，并清除本机轮换偏移", monday);
        return true;
    }
}

/// <summary>从信封中取出学期设置；单独成类以便不经网络直接测试。</summary>
public static class SemesterMessage
{
    public static bool TryRead(Envelope envelope, out SemesterSync sync)
    {
        sync = null!;
        if (envelope.Type != Protocol.MessageTypeSemester || envelope.Payload is null) return false;
        try
        {
            sync = JsonSerializer.Deserialize<SemesterSync>(JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return sync is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// SingleWeekStartTime 与 MultiWeekRotationOffset 位于宿主 Settings 上（不在插件 SDK 接口中），
/// 经 ProfileService.SettingsService 按名称反射访问；宿主改名或移除时 <see cref="Apply"/> 返回 false。
/// </summary>
public sealed class ClassIslandSemesterHost : ISemesterHostOperations
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Func<object?> _settingsService;

    public ClassIslandSemesterHost(IProfileService profiles)
        : this(() => profiles.GetType().GetProperty("SettingsService", Flags)?.GetValue(profiles)) { }

    internal ClassIslandSemesterHost(Func<object?> settingsService) => _settingsService = settingsService;

    private object? SettingsService => _settingsService();
    private object? Settings => SettingsService is { } service ? service.GetType().GetProperty("Settings", Flags)?.GetValue(service) : null;
    private PropertyInfo? StartProperty => Settings?.GetType().GetProperty("SingleWeekStartTime", BindingFlags.Instance | BindingFlags.Public) is
        { CanRead: true, CanWrite: true } property && property.PropertyType == typeof(DateTime) ? property : null;
    private IList? Offsets => Settings?.GetType().GetProperty("MultiWeekRotationOffset", BindingFlags.Instance | BindingFlags.Public)?.GetValue(Settings) as IList;

    public DateTime? SingleWeekStartTime => StartProperty?.GetValue(Settings) as DateTime?;

    // 偏移列表下标为轮换周数（从 2 开始），前两项是占位的 -1。
    public bool HasRotationOffsets => Offsets is { } offsets && offsets.Cast<object?>().Skip(2).Any(x => x is int value && value != 0);

    public bool Apply(DateTime weekOneMonday)
    {
        var service = SettingsService;
        var settings = Settings;
        if (service is null || settings is null || StartProperty is not { } start) return false;
        start.SetValue(settings, weekOneMonday.Date);
        Offsets?.Clear();
        // ClassIsland 2.1 只有 SaveSettings(string note)，note 仅用于宿主日志；无参形式留给其他宿主版本。
        if (service.GetType().GetMethod("SaveSettings", Flags, binder: null, types: [typeof(string)], modifiers: null) is { } withNote)
            withNote.Invoke(service, ["RemoteCI 同步学期开始日期"]);
        else
            service.GetType().GetMethod("SaveSettings", Flags, binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(service, null);
        return true;
    }
}
