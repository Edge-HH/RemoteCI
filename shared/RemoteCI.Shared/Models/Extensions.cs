using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 扩展 Id 的统一领域规则。Id 会跨插件注册表、协议和数据库作为精确匹配键，
/// 因此禁止隐式裁剪或在不同端采用不同长度限制。
/// </summary>
public readonly record struct ExtensionId
{
    public const int MaxLength = 200;

    private ExtensionId(string value) => Value = value;

    public string Value { get; }

    public static ExtensionId Parse(string? value, string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("扩展 Id 不能为空", paramName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("扩展 Id 不能包含首尾空白", paramName);
        if (value.Length > MaxLength)
            throw new ArgumentException($"扩展 Id 不能超过 {MaxLength} 个字符", paramName);
        return new ExtensionId(value);
    }

    public override string ToString() => Value;
}

/// <summary>
/// 插件注册的自定义远程功能元数据，经 extensions_sync 同步给服务端与手表。
/// 手表端据此在控制菜单底部渲染入口和参数表单。
/// </summary>
public sealed class ExtensionDefinition
{
    /// <summary>扩展归属班级；旧版服务端不下发该字段。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    /// <summary>全局唯一扩展 Id，命令路由与去重都使用它；格式由 <see cref="ExtensionId"/> 统一约束。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>手表菜单显示的文案。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>可选 Material 图标名；手表端命中白名单时显示图标，否则纯文字。白名单见文档站「接入扩展 - 扩展图标名」。</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    /// <summary>兼容旧扩展的声明字段；当前统一由 RunExtensions 和服务端扩展策略鉴权。</summary>
    [JsonPropertyName("requiredPermission")]
    public UserPermissions RequiredPermission { get; set; }

    /// <summary>可选参数表单描述；为空时手表点击后直接执行。</summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionParameter>? Parameters { get; set; }

    /// <summary>可选功能说明；WebUI 在扩展卡片上展示，手表忽略。</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>可选所属扩展分组 Id（通常对应一个 ClassIsland 插件）；WebUI 按分组归类展示，手表忽略。</summary>
    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }
}

/// <summary>
/// 扩展分组：一个 ClassIsland 插件向 RemoteCI 声明的分组与设置页，经 extension_groups_sync 同步给服务端。
/// 同一分组下的扩展功能在 WebUI 控制与批量控制中归在一起；声明了设置字段的分组还拥有独立的设置页。
/// </summary>
public sealed class ExtensionGroupDefinition
{
    /// <summary>分组归属班级；由服务端按插件凭据回填，插件上报时不需要填写。</summary>
    [JsonPropertyName("classId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ClassId { get; set; }

    /// <summary>全局唯一分组 Id；格式规则与扩展 Id 相同。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>可选图标名，取值与扩展图标相同；WebUI 暂按 Material 名称展示占位图标。</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    /// <summary>设置字段描述；为空表示该分组只用于归类扩展功能，没有设置页。</summary>
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionParameter>? Settings { get; set; }

    /// <summary>设备上的当前设置值；键为设置字段 Key，值统一以字符串传输。</summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Values { get; set; }

    [JsonIgnore]
    public bool HasSettings => Settings is { Count: > 0 };
}

/// <summary>ApplyExtensionSettings 命令载荷：只包含要修改的设置字段，未出现的字段保持设备当前值。</summary>
public sealed class ExtensionSettingsRequest
{
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>扩展参数表单的单个字段描述（schema 驱动，手表通用渲染）。</summary>
public sealed class ExtensionParameter
{
    /// <summary>参数键，命令中 extensionArgs 的 key。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>手表表单上展示的字段名。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public ExtensionParameterType Type { get; set; } = ExtensionParameterType.Text;

    /// <summary>默认值；switch 使用 "true"/"false"，其余使用字符串。</summary>
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>select 类型的候选项。</summary>
    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Options { get; set; }

    /// <summary>与 Options 按下标一一对应的显示名称；缺失或数量不一致时直接显示候选项原值。手表暂显示原值。</summary>
    [JsonPropertyName("optionLabels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OptionLabels { get; set; }

    /// <summary>可选字段说明，WebUI 显示在输入框下方。</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>可选占位提示，仅 text/number 类型使用。</summary>
    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; set; }

    /// <summary>text 类型在 WebUI 中使用多行输入框；手表仍为单行输入。</summary>
    [JsonPropertyName("multiline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Multiline { get; set; }

    /// <summary>number 类型的最小值（含）。</summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    /// <summary>number 类型的最大值（含）。</summary>
    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    /// <summary>number 类型的步长（仅影响输入控件，不参与校验）。</summary>
    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Step { get; set; }

    /// <summary>number 类型在 WebUI 中同时显示滑块（需同时声明 Min 与 Max）；手表忽略。</summary>
    [JsonPropertyName("slider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Slider { get; set; }

    /// <summary>
    /// 可选分区标题（WebUI 设置页布局，手表忽略）。连续出现的同名分区归为一段，以小标题分隔，
    /// 对应插件设置界面里的分节标题；字段按声明顺序渲染。
    /// </summary>
    [JsonPropertyName("section")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Section { get; set; }

    /// <summary>
    /// 可选卡片标题（WebUI 设置页布局）。同一分区内连续出现的同名卡片归为一张可折叠卡片，
    /// 对应插件设置界面里的一个设置组；未声明卡片的字段单独成行。
    /// </summary>
    [JsonPropertyName("card")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Card { get; set; }

    /// <summary>卡片说明；同一卡片取第一个非空值。</summary>
    [JsonPropertyName("cardDescription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardDescription { get; set; }

    /// <summary>
    /// switch 字段作为所在卡片的总开关，渲染在卡片标题行。只决定摆放位置：
    /// 卡片内其它字段是否随开关禁用，由各自的 <see cref="EnabledWhen"/> 显式声明。
    /// </summary>
    [JsonPropertyName("cardToggle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CardToggle { get; set; }

    /// <summary>
    /// 显示条件（全部满足才显示，条件引用同一表单内其它字段的当前值）。隐藏的字段仍按原值提交，
    /// 因此不会被意外清空；卡片内字段全部隐藏时整张卡片隐藏。
    /// </summary>
    [JsonPropertyName("visibleWhen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionFieldCondition>? VisibleWhen { get; set; }

    /// <summary>可编辑条件（全部满足才可编辑）；不满足时字段置灰但仍按原值提交。</summary>
    [JsonPropertyName("enabledWhen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionFieldCondition>? EnabledWhen { get; set; }

    /// <summary>
    /// 带卡片名的完整字段名（如“阴影 · 阴影颜色”），用于表格列头、待补发摘要与校验错误等脱离卡片上下文的场景。
    /// </summary>
    public string QualifiedLabel()
    {
        var label = string.IsNullOrWhiteSpace(Label) ? Key : Label;
        return string.IsNullOrWhiteSpace(Card) || string.Equals(Card, label, StringComparison.Ordinal)
            ? label
            : $"{Card} · {label}";
    }

    /// <summary>候选项在界面上的显示名称：优先 OptionLabels 中的同位置名称，否则为候选项原值。</summary>
    public string OptionLabel(int index)
    {
        var options = Options ?? [];
        if (index < 0 || index >= options.Count) return string.Empty;
        return OptionLabels is { } labels && labels.Count == options.Count && !string.IsNullOrWhiteSpace(labels[index])
            ? labels[index]
            : options[index];
    }
}

/// <summary>扩展参数类型。</summary>
public enum ExtensionParameterType
{
    Text = 1,
    Number = 2,
    Switch = 3,
    Select = 4,

    /// <summary>
    /// 颜色，值为 #RGB / #ARGB / #RRGGBB / #AARRGGBB（可带透明度）。WebUI 渲染取色器 + 透明度 + 色卡；
    /// 不认识该类型的旧版服务端与手表按文本处理。
    /// </summary>
    Color = 5,
}

/// <summary>
/// 字段联动条件：引用同一表单内另一个字段（<see cref="Key"/>）的当前值。
/// 当前值属于 <see cref="Values"/> 之一即为满足（数字按数值比较，其余忽略大小写），<see cref="Not"/> 时取反。
/// </summary>
public sealed class ExtensionFieldCondition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];

    [JsonPropertyName("not")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Not { get; set; }

    public bool Matches(string? value)
    {
        var hit = Values.Any(candidate => ValueEquals(candidate, value));
        return Not ? !hit : hit;
    }

    /// <summary>全部条件满足（无条件视为满足）；<paramref name="lookup"/> 按字段 Key 取当前值。</summary>
    public static bool All(IEnumerable<ExtensionFieldCondition>? conditions, Func<string, string?> lookup) =>
        conditions is null || conditions.All(condition => condition.Matches(lookup(condition.Key)));

    private static bool ValueEquals(string candidate, string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return true;
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return double.TryParse(candidate, style, culture, out var a) &&
            double.TryParse(value, style, culture, out var b) &&
            a == b;
    }
}

/// <summary>颜色字段的解析：支持 #RGB / #ARGB / #RRGGBB / #AARRGGBB，缺省透明度视为不透明。</summary>
public static class ExtensionColor
{
    public static bool TryParse(string? value, out (byte A, byte R, byte G, byte B) color)
    {
        color = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '#') return false;
        var hex = text[1..];
        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(Uri.IsHexDigit)) return false;
        if (hex.Length <= 4)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length == 6) hex = "FF" + hex;
        var argb = Convert.ToUInt32(hex, 16);
        color = ((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return true;
    }

    /// <summary>规范为 #AARRGGBB（大写），非法值返回 null。</summary>
    public static string? Normalize(string? value) =>
        TryParse(value, out var c) ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : null;
}

/// <summary>
/// 扩展参数与扩展设置字段的统一校验：服务端预检与插件执行端共用同一套规则，
/// 返回的错误文案直接展示给发起操作的用户。
/// </summary>
public static class ExtensionFieldValidator
{
    /// <summary>单个值的长度上限，避免超大载荷击穿消息缓冲或塞满扩展日志。</summary>
    public const int MaxValueLength = 4096;

    /// <summary>
    /// 校验扩展执行参数：声明过的参数按类型检查并规范化（switch 统一为小写 true/false），
    /// 未声明的键原样透传以兼容按自定义键读取参数的旧扩展；可选参数缺失时不补默认值。
    /// </summary>
    public static Dictionary<string, string?> ValidateArguments(
        IReadOnlyList<ExtensionParameter> parameters,
        IReadOnlyDictionary<string, string?>? submitted,
        out string? error)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in submitted ?? new Dictionary<string, string?>()) values[pair.Key] = pair.Value;

        var missing = parameters
            .Where(p => p.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Key)))
            .Select(p => DisplayName(p))
            .ToList();
        if (missing.Count > 0)
        {
            error = $"缺少参数：{string.Join("、", missing)}";
            return values;
        }
        if (values.Any(pair => pair.Value is { Length: > MaxValueLength }))
        {
            error = $"扩展参数过长（单个参数不能超过 {MaxValueLength} 个字符）";
            return values;
        }
        foreach (var parameter in parameters)
        {
            if (!values.TryGetValue(parameter.Key, out var value)) continue;
            if ((error = Normalize(parameter, ref value)) is not null) return values;
            values[parameter.Key] = value;
        }
        error = null;
        return values;
    }

    /// <summary>
    /// 校验扩展设置的部分更新：只允许声明过的字段，至少包含一个字段，必填字段不能清空。
    /// 返回规范化后的待修改字段；未出现的字段保持设备当前值。
    /// </summary>
    public static Dictionary<string, string?> ValidateSettings(
        IReadOnlyList<ExtensionParameter> fields,
        IReadOnlyDictionary<string, string?>? submitted,
        out string? error)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (submitted is null || submitted.Count == 0)
        {
            error = "请至少选择一项要修改的设置";
            return values;
        }
        var declared = fields.ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (var pair in submitted)
        {
            if (!declared.TryGetValue(pair.Key, out var field))
            {
                error = $"设置项不存在：{pair.Key}";
                return values;
            }
            var value = pair.Value;
            if (value is { Length: > MaxValueLength })
            {
                error = $"“{DisplayName(field)}”过长（不能超过 {MaxValueLength} 个字符）";
                return values;
            }
            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                error = $"“{DisplayName(field)}”不能为空";
                return values;
            }
            if ((error = Normalize(field, ref value)) is not null) return values;
            values[pair.Key] = value;
        }
        error = null;
        return values;
    }

    private static string? Normalize(ExtensionParameter field, ref string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        switch (field.Type)
        {
            case ExtensionParameterType.Number:
                if (!double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                    double.IsNaN(number) || double.IsInfinity(number))
                    return $"“{DisplayName(field)}”必须是数字";
                if (number < field.Min || number > field.Max)
                    return $"“{DisplayName(field)}”需在 {FormatRange(field)} 之间";
                value = value.Trim();
                return null;
            case ExtensionParameterType.Switch:
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) value = "true";
                else if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) value = "false";
                else return $"“{DisplayName(field)}”只能是开启或关闭";
                return null;
            case ExtensionParameterType.Select:
                return field.Options is { Count: > 0 } options && !options.Contains(value, StringComparer.Ordinal)
                    ? $"“{DisplayName(field)}”不是有效选项"
                    : null;
            case ExtensionParameterType.Color:
                if (!ExtensionColor.TryParse(value, out _))
                    return $"“{DisplayName(field)}”必须是 #RRGGBB 或 #AARRGGBB 格式的颜色";
                value = value.Trim();
                return null;
            default:
                return null;
        }
    }

    private static string DisplayName(ExtensionParameter field) => field.QualifiedLabel();

    private static string FormatRange(ExtensionParameter field) =>
        $"{field.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-∞"} 到 " +
        $"{field.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "+∞"}";
}

/// <summary>扩展调用权限的统一判定，供服务端、插件和客户端保持一致。</summary>
public static class ExtensionAccess
{
    /// <summary>按有效权限位与允许列表判定；供班级上下文等没有完整 Profile 的场景使用。</summary>
    public static bool CanInvoke(
        UserPermissions permissions, IEnumerable<string>? allowedExtensionIds, ExtensionDefinition extension)
    {
        if (!permissions.HasFlag(UserPermissions.RunExtensions)) return false;
        return allowedExtensionIds is null || allowedExtensionIds.Contains(extension.Id, StringComparer.Ordinal);
    }

    public static bool CanInvoke(UserProfile? user, ExtensionDefinition extension) =>
        CanInvoke(user?.Permissions ?? UserPermissions.None, user?.AllowedExtensionIds, extension);

    public static bool IsVisibleOnWatch(UserProfile? user, ExtensionDefinition extension) =>
        CanInvoke(user, extension) &&
        (user!.VisibleExtensionIds is null || user.VisibleExtensionIds.Contains(extension.Id, StringComparer.Ordinal));
}
