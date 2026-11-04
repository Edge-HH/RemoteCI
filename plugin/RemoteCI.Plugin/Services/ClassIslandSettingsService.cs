using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 远程读写 ClassIsland 设置。宿主的 <c>SettingsService</c>/<c>Settings</c> 不在插件 SDK 中，
/// 按类型名反射访问；能读写哪些项由共享目录 <see cref="ClassIslandSettingsCatalog"/> 约束，
/// 服务端 WebUI 表单使用同一份目录。读取只返回目录内的项，写入只更新载荷给出的项。
/// </summary>
public sealed class ClassIslandSettingsService(IServiceProvider? services, ILogger<ClassIslandSettingsService> logger)
{
    private const BindingFlags PropertyFlags = BindingFlags.Instance | BindingFlags.Public;

    private volatile object? _cachedService;

    /// <summary>读取目录内全部设置项的当前值；宿主不支持或属性缺失的项省略。</summary>
    public async Task<CommandResult> ReadAsync()
    {
        if (!HasHost)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported,
                "当前 ClassIsland 版本不支持读取设置");
        var result = await Dispatcher.UIThread.InvokeAsync(ReadOnUi);
        return result ?? CommandResult.Failure(CommandResultCodes.CapabilityUnsupported,
            "当前 ClassIsland 版本不支持读取设置");
    }

    /// <summary>部分更新：只写入 values 中出现的设置项，其余保持设备当前值。</summary>
    public async Task<CommandResult> ApplyAsync(ClassIslandSettingsRequest? request)
    {
        if (request?.Values is not { Count: > 0 } values)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "没有要修改的设置项");
        if (ClassIslandSettingsCatalog.ValidatePatch(values) is { } invalid)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, invalid);
        if (!HasHost)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported,
                "当前 ClassIsland 版本不支持修改设置");
        var applied = await Dispatcher.UIThread.InvokeAsync(() => ApplyOnUi(values));
        if (applied is null)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported,
                "当前 ClassIsland 版本不支持修改设置");
        if (applied.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "设备不支持所修改的设置项");
        var skipped = values.Keys.Except(applied, StringComparer.Ordinal).ToList();
        var message = skipped.Count > 0
            ? $"已更新 {applied.Count} 项设置；{string.Join("、", skipped)} 在当前设备上不可用"
            : $"已更新 {applied.Count} 项设置";
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = message,
        };
    }

    private CommandResult? ReadOnUi()
    {
        if (GetSettingsObject() is not { } settings) return null;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var options = JsonDefaults.Options;
        foreach (var descriptor in ClassIslandSettingsCatalog.Items)
        {
            try
            {
                var property = settings.GetType().GetProperty(descriptor.Key, PropertyFlags);
                if (property is not { CanRead: true } || property.GetValue(settings) is not { } raw) continue;
                if (ToJson(descriptor, raw, options) is not { } value) continue;
                values[descriptor.Key] = value;
            }
            catch (Exception ex)
            {
                // 单项读取失败不影响其他设置项；该项在 WebUI 中保持默认值。
                logger.LogDebug(ex, "读取 ClassIsland 设置 {Key} 失败", descriptor.Key);
            }
        }
        var snapshot = new ClassIslandSettingsSnapshot { Values = values };
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = "已读取当前 ClassIsland 设置",
            Data = JsonSerializer.Serialize(snapshot, options),
        };
    }

    /// <summary>实际写入的键集合；null 表示宿主不可用，空集合表示没有任何键写入成功。</summary>
    private HashSet<string>? ApplyOnUi(Dictionary<string, JsonElement> values)
    {
        if (GetSettingsObject() is not { } settings) return null;
        var service = _cachedService;
        var applied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, json) in values)
        {
            if (ClassIslandSettingsCatalog.Find(key) is not { } descriptor) continue;
            try
            {
                var property = settings.GetType().GetProperty(key, PropertyFlags);
                if (property is not { CanWrite: true } ||
                    ToSettingValue(descriptor, property.PropertyType, json, property.GetValue(settings)) is not { } value) continue;
                property.SetValue(settings, value);
                applied.Add(key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "写入 ClassIsland 设置 {Key} 失败", key);
            }
        }
        if (applied.Count > 0) SaveSettings(service);
        return applied;
    }

    /// <summary>显式保存一次；宿主 2.1 只有带日志备注的重载，无参形式留给其他宿主版本。</summary>
    private void SaveSettings(object? service)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (service?.GetType().GetMethod("SaveSettings", flags, binder: null, types: [typeof(string)], modifiers: null)
                is { } withNote)
                withNote.Invoke(service, ["RemoteCI 修改 ClassIsland 设置"]);
            else
                service?.GetType().GetMethod("SaveSettings", flags, binder: null, types: Type.EmptyTypes, modifiers: null)
                    ?.Invoke(service, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "保存 ClassIsland 设置失败；修改可能未写入 Settings.json");
        }
    }

    private object? GetSettingsObject()
    {
        var service = _cachedService ??= ResolveService();
        if (service is null) return null;
        return service.GetType().GetProperty("Settings", PropertyFlags)?.GetValue(service);
    }

    /// <summary>宿主 SettingsService 已解析且 Settings 对象可读；解析失败的结果会被缓存复用。</summary>
    private bool HasHost => GetSettingsObject() is not null;

    /// <summary>宿主 SettingsService 类型不在插件 SDK 中，按类型名在已加载程序集里解析一次并缓存。</summary>
    private object? ResolveService()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("ClassIsland.Services.SettingsService", throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        if (type is null || services?.GetService(type) is not { } service)
        {
            logger.LogWarning("当前 ClassIsland 版本没有可用的 SettingsService");
            return null;
        }
        return service;
    }

    /// <summary>把设置值转换为 JSON；转换失败返回 null（该项省略）。</summary>
    internal static JsonElement? ToJson(ClassIslandSettingDescriptor descriptor, object raw, JsonSerializerOptions options)
    {
        switch (descriptor.Type)
        {
            case ClassIslandSettingType.Boolean when raw is bool flag:
                return JsonSerializer.SerializeToElement(flag, options);
            case ClassIslandSettingType.Integer or ClassIslandSettingType.Enum:
            {
                var intValue = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return JsonSerializer.SerializeToElement(intValue, options);
            }
            case ClassIslandSettingType.Double:
                return JsonSerializer.SerializeToElement(Convert.ToDouble(raw, CultureInfo.InvariantCulture), options);
            case ClassIslandSettingType.String when raw is string text:
                return JsonSerializer.SerializeToElement(text, options);
            case ClassIslandSettingType.Color:
                return JsonSerializer.SerializeToElement(ColorToHex(raw), options);
            case ClassIslandSettingType.Date when raw is DateTime date:
                return JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), options);
            case ClassIslandSettingType.StringList when raw is IEnumerable items:
            {
                var list = items.Cast<object?>()
                    .Select(item => item?.ToString() ?? string.Empty)
                    .ToList();
                return JsonSerializer.SerializeToElement(list, options);
            }
            default:
                return null;
        }
    }

    /// <summary>把 JSON 值转换为宿主属性类型；转换失败返回 null（该项跳过并计入“不可用”）。</summary>
    internal static object? ToSettingValue(ClassIslandSettingDescriptor descriptor, Type propertyType, JsonElement json, object? current)
    {
        switch (descriptor.Type)
        {
            case ClassIslandSettingType.Boolean when json.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return json.GetBoolean();
            case ClassIslandSettingType.Integer or ClassIslandSettingType.Enum:
                if (!TryGetIntegral(json, out var intValue)) return null;
                return propertyType.IsEnum ? Enum.ToObject(propertyType, intValue)
                    : Convert.ChangeType(intValue, propertyType, CultureInfo.InvariantCulture);
            case ClassIslandSettingType.Double:
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetDouble(out var doubleValue)) return null;
                return Convert.ChangeType(doubleValue, Nullable.GetUnderlyingType(propertyType) ?? propertyType,
                    CultureInfo.InvariantCulture);
            case ClassIslandSettingType.String when json.ValueKind == JsonValueKind.String:
                return json.GetString();
            case ClassIslandSettingType.Color when json.ValueKind == JsonValueKind.String:
                return ParseColor(propertyType, json.GetString() ?? string.Empty);
            case ClassIslandSettingType.Date when json.ValueKind == JsonValueKind.String:
                if (!DateTime.TryParseExact(json.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)) return null;
                return date.Date;
            case ClassIslandSettingType.StringList when json.ValueKind == JsonValueKind.Array:
            {
                // 原地改写现有集合：宿主可能持有集合引用（如规则集评估），替换属性值会与之脱节。
                IList? list = current as IList;
                if (list is null && Activator.CreateInstance(propertyType) is IList created) list = created;
                if (list is null) return null;
                list.Clear();
                foreach (var item in json.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    list.Add(item.GetString() ?? string.Empty);
                }
                return list;
            }
            default:
                return null;
        }
    }

    private static bool TryGetIntegral(JsonElement json, out int value)
    {
        value = 0;
        if (json.ValueKind != JsonValueKind.Number) return false;
        if (json.TryGetInt32(out value)) return true;
        // 允许 1.0 这类来自 JS 的整数值浮点。
        if (json.TryGetDouble(out var doubleValue) && Math.Abs(doubleValue - Math.Round(doubleValue)) < double.Epsilon)
        {
            value = (int)Math.Round(doubleValue);
            return true;
        }
        return false;
    }

    /// <summary>Avalonia 的 Color 结构不在插件 SDK 中，按静态 Parse/FromArgb 与属性类型反射转换。</summary>
    private static object? ParseColor(Type propertyType, string text)
    {
        if (ClassIslandSettingsCatalog.IsValidColor(text) is false) return null;
        var normalized = text.Length switch
        {
            4 => $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}",   // #RGB
            5 => $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}{text[4]}{text[4]}", // #ARGB（A 保留原值）
            _ => text,
        };
        return InvokeColorFactory(propertyType, "Parse", [typeof(string)], [normalized])
            ?? FromArgb(propertyType, normalized);
    }

    private static object? FromArgb(Type propertyType, string hex)
    {
        var digits = hex.TrimStart('#');
        byte a = 255, r, g, b;
        if (digits.Length == 8)
        {
            a = Convert.ToByte(digits[..2], 16);
            digits = digits[2..];
        }
        r = Convert.ToByte(digits[..2], 16);
        g = Convert.ToByte(digits.Substring(2, 2), 16);
        b = Convert.ToByte(digits.Substring(4, 2), 16);
        return InvokeColorFactory(propertyType, "FromArgb", [typeof(byte), typeof(byte), typeof(byte), typeof(byte)],
            [a, r, g, b]);
    }

    private static object? InvokeColorFactory(Type propertyType, string name, Type[] parameterTypes, object[] arguments) =>
        propertyType.GetMethod(name, BindingFlags.Public | BindingFlags.Static, binder: null,
            types: parameterTypes, modifiers: null)?.Invoke(null, arguments);

    /// <summary>读取 Avalonia Color 的 A/R/G/B 字节，输出 #RRGGBB（不透明）或 #AARRGGBB。</summary>
    private static string ColorToHex(object raw)
    {
        var type = raw.GetType();
        var a = GetColorByte(type, raw, "A");
        var r = GetColorByte(type, raw, "R");
        var g = GetColorByte(type, raw, "G");
        var b = GetColorByte(type, raw, "B");
        return a is 255 or null
            ? $"#{r:x2}{g:x2}{b:x2}"
            : $"#{a:x2}{r:x2}{g:x2}{b:x2}";
    }

    private static byte? GetColorByte(Type type, object raw, string name) =>
        type.GetProperty(name, PropertyFlags)?.GetValue(raw) is IConvertible convertible
            ? convertible.ToByte(CultureInfo.InvariantCulture)
            : null;
}
