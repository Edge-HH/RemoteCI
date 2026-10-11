using System.Text.Json;
using RemoteCI.Shared;
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
    bool Selectable = false)
{
    /// <summary>颜色字段的色卡：常用色，点选后保留当前透明度、只替换 RGB。</summary>
    public static IReadOnlyList<string> ColorSwatches { get; } =
    [
        "#FFFFFF", "#000000", "#202020", "#7DD3FC", "#4383EE", "#22C55E",
        "#FACC15", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#94A3B8",
    ];

    /// <summary>字段的预填值：设备当前值优先，其次字段默认值。</summary>
    public string? ValueOf(ExtensionParameter field) =>
        Values is { } values && values.TryGetValue(field.Key, out var current) ? current : field.DefaultValue;

    /// <summary>按 Key 取预填值，供联动条件在服务端预先求值（避免脚本加载前闪烁）。</summary>
    public string? ValueOf(string key) =>
        Fields.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal)) is { } field
            ? ValueOf(field)
            : null;

    public bool IsVisible(ExtensionParameter field) => ExtensionFieldCondition.All(field.VisibleWhen, ValueOf);

    public bool IsEnabled(ExtensionParameter field) => ExtensionFieldCondition.All(field.EnabledWhen, ValueOf);

    /// <summary>联动条件序列化为 data 属性，由 extension-fields.js 在表单变化时重新求值。</summary>
    public static string? ConditionJson(List<ExtensionFieldCondition>? conditions) =>
        conditions is { Count: > 0 } ? JsonSerializer.Serialize(conditions, JsonDefaults.Options) : null;

    /// <summary>
    /// 按分区 → 卡片归组（保持声明顺序）：连续的同名分区成一段、段内连续的同名卡片成一张卡片，
    /// 未声明卡片的字段各自成行。没有任何字段声明布局时得到单段、每字段一行，渲染与旧版一致。
    /// </summary>
    public IReadOnlyList<ExtensionFieldSection> BuildLayout()
    {
        var sections = new List<ExtensionFieldSection>();
        for (var index = 0; index < Fields.Count; index++)
        {
            var field = Fields[index];
            var sectionTitle = Blank(field.Section);
            if (sections.Count == 0 || !string.Equals(sections[^1].Title, sectionTitle, StringComparison.Ordinal))
                sections.Add(new ExtensionFieldSection(sectionTitle));
            var section = sections[^1];

            var cardTitle = Blank(field.Card);
            var card = section.Cards.Count > 0 ? section.Cards[^1] : null;
            if (cardTitle is null || card is null || !string.Equals(card.Title, cardTitle, StringComparison.Ordinal))
            {
                card = new ExtensionFieldCard(cardTitle);
                section.Cards.Add(card);
            }
            card.Description ??= Blank(field.CardDescription);
            // 卡片开关只取第一个声明为 CardToggle 的 switch 字段，其余照常作为卡片内条目。
            if (cardTitle is not null && field.CardToggle && field.Type == ExtensionParameterType.Switch && card.Toggle is null)
                card.Toggle = new ExtensionFieldSlot(index, field);
            else
                card.Items.Add(new ExtensionFieldSlot(index, field));
        }
        return sections;
    }

    /// <summary>是否声明了任何分区或卡片（决定是否使用设置页式布局）。</summary>
    public bool HasLayout => Fields.Any(x => !string.IsNullOrWhiteSpace(x.Section) || !string.IsNullOrWhiteSpace(x.Card));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>设置页中的一个分区（Title 为空表示未分区的字段）。</summary>
public sealed record ExtensionFieldSection(string? Title)
{
    public List<ExtensionFieldCard> Cards { get; } = [];
}

/// <summary>分区中的一张卡片；Title 为空表示单独成行的字段（此时只有一个条目）。</summary>
public sealed record ExtensionFieldCard(string? Title)
{
    public string? Description { get; set; }

    public ExtensionFieldSlot? Toggle { get; set; }

    public List<ExtensionFieldSlot> Items { get; } = [];

    /// <summary>卡片内全部字段（含标题行开关），用于判断整张卡片是否可见。</summary>
    public IEnumerable<ExtensionFieldSlot> AllSlots => Toggle is { } toggle ? Items.Prepend(toggle) : Items;
}

/// <summary>字段及其在 Fields 中的下标（表单字段名依赖下标，必须与声明顺序一致）。</summary>
public sealed record ExtensionFieldSlot(int Index, ExtensionParameter Field);

/// <summary>分部视图渲染单个字段时的输入：Compact 表示放在卡片标题行（只渲染控件）。</summary>
public sealed record ExtensionFieldRender(ExtensionFieldsModel Form, ExtensionFieldSlot Slot, bool Compact = false);

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
