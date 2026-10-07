using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 扩展参数与扩展设置字段的通用表单渲染输入（_ExtensionFields 分部视图）。
/// Prefix 决定提交字段名：{Prefix}[i].Key / .Value / .Apply；Selectable 时每个字段带“修改此项”勾选框，用于批量部分修改。
/// </summary>
public sealed record ExtensionFieldsModel(
    IReadOnlyList<ExtensionParameter> Fields,
    string Prefix,
    string IdPrefix,
    IReadOnlyDictionary<string, string?>? Values = null,
    bool Selectable = false);

/// <summary>表单提交的单个扩展字段值；Apply 只在批量部分修改时使用。</summary>
public sealed class ExtensionFieldInput
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool Apply { get; set; }

    /// <summary>转为键值字典；onlyApplied 时只保留勾选了“修改此项”的字段。重复键以最后一次提交为准。</summary>
    public static Dictionary<string, string?> ToValues(IEnumerable<ExtensionFieldInput> inputs, bool onlyApplied = false) =>
        inputs
            .Where(input => !string.IsNullOrWhiteSpace(input.Key) && (!onlyApplied || input.Apply))
            .GroupBy(input => input.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
}
