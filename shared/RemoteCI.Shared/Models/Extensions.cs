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
}

/// <summary>扩展参数类型。</summary>
public enum ExtensionParameterType
{
    Text = 1,
    Number = 2,
    Switch = 3,
    Select = 4,
}

/// <summary>扩展调用权限的统一判定，供服务端、插件和客户端保持一致。</summary>
public static class ExtensionAccess
{
    public static bool CanInvoke(UserProfile? user, ExtensionDefinition extension)
    {
        if (user is null || !user.Permissions.HasFlag(UserPermissions.RunExtensions)) return false;
        return user.AllowedExtensionIds is null || user.AllowedExtensionIds.Contains(extension.Id, StringComparer.Ordinal);
    }

    public static bool IsVisibleOnWatch(UserProfile? user, ExtensionDefinition extension) =>
        CanInvoke(user, extension) &&
        (user!.VisibleExtensionIds is null || user.VisibleExtensionIds.Contains(extension.Id, StringComparer.Ordinal));
}
