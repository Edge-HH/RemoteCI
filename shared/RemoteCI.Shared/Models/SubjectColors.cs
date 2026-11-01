using System.Globalization;
using System.Text;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 学科颜色：课表、日程表格与列表使用的低饱和配色。按去首尾空白的学科名对应；用户可从 <see cref="Palette"/> 中改色（按账号保存），
/// 未改过的学科由 <see cref="Default"/> 确定性地分配，WebUI 与手机端算出的结果必须一致（Android 端 SubjectColors.kt 是本类的移植，
/// 两端共用 shared/test-fixtures/subject-colors.json 校验）。
/// </summary>
public static class SubjectColors
{
    /// <summary>可选的 18 种基准色（色板展示用）；实际色块的底色与文字色由 <see cref="Derive"/> 按浅色/深色模式派生。</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#6F9FE0", // 0 蓝
        "#E58C8C", // 1 珊瑚红
        "#9B8BDB", // 2 紫
        "#66BFA0", // 3 青绿
        "#E9A55F", // 4 橙
        "#5FB8CC", // 5 青
        "#DC8FC2", // 6 粉
        "#9CC06A", // 7 草绿
        "#E3967A", // 8 蜜桃
        "#8297DA", // 9 长春花
        "#C59E76", // 10 驼
        "#72B3E6", // 11 天蓝
        "#B48AD8", // 12 紫罗兰
        "#E2BC57", // 13 金
        "#78C495", // 14 薄荷
        "#D9879E", // 15 玫瑰
        "#86A9B5", // 16 石板
        "#C79BDF", // 17 丁香
    ];

    /// <summary>常见学科的固定颜色（色板下标）；顺序也是名称包含匹配的优先顺序，例如“体育与健康”匹配“体育”。</summary>
    private static readonly (string Name, int Swatch)[] Builtin =
    [
        ("语文", 1),
        ("数学", 0),
        ("英语", 2),
        ("物理", 4),
        ("化学", 3),
        ("生物", 7),
        ("道德与法治", 15),
        ("思想政治", 15),
        ("政治", 15),
        ("道法", 15),
        ("历史", 10),
        ("地理", 5),
        ("信息", 11),
        ("通用技术", 16),
        ("体育", 14),
        ("音乐", 6),
        ("美术", 8),
        ("心理", 17),
        ("班会", 13),
        ("自习", 9),
        ("劳动", 12),
        ("日语", 12),
        ("科学", 3),
        ("书法", 10),
    ];

    /// <summary>颜色对应用的学科名：去首尾空白；空白名返回空字符串。</summary>
    public static string Key(string? subject) => subject?.Trim() ?? string.Empty;

    /// <summary>未改过颜色的学科的默认基准色：常见学科用固定色，其余按名称的 FNV-1a 哈希落入色板。</summary>
    public static string Default(string? subject) => Palette[DefaultSwatch(subject)];

    public static int DefaultSwatch(string? subject)
    {
        var name = Key(subject);
        foreach (var (builtin, swatch) in Builtin)
            if (name == builtin) return swatch;
        foreach (var (builtin, swatch) in Builtin)
            if (name.Contains(builtin, StringComparison.Ordinal)) return swatch;
        return (int)(Fnv1a(name) % (uint)Palette.Count);
    }

    /// <summary>颜色是否是色板中的一种（大小写不敏感）。</summary>
    public static bool IsPaletteColor(string? color) =>
        color is not null && Palette.Any(x => x.Equals(color.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>规范为色板中的写法（大写 #RRGGBB）；不在色板中返回 null。</summary>
    public static string? NormalizePaletteColor(string? color) =>
        color is null ? null : Palette.FirstOrDefault(x => x.Equals(color.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 由基准色派生色块的底色与文字色：保持色相，浅色模式为浅底深字，深色模式为暗底浅字。
    /// 计算只用加减乘除与 floor(x + 0.5) 取整，保证与 Kotlin 移植逐位一致。
    /// </summary>
    public static (string Background, string Foreground) Derive(string baseColor, bool dark)
    {
        var (h, s, _) = ToHsl(baseColor);
        return dark
            ? (FromHsl(h, s * 0.45, 0.24), FromHsl(h, Math.Min(s * 0.8, 0.75), 0.84))
            : (FromHsl(h, Math.Min(s * 0.9, 0.7), 0.92), FromHsl(h, Math.Min(s, 0.5), 0.3));
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }
        return hash;
    }

    private static (double H, double S, double L) ToHsl(string hex)
    {
        var value = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var r = ((value >> 16) & 0xFF) / 255.0;
        var g = ((value >> 8) & 0xFF) / 255.0;
        var b = (value & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return "#" + Channel(r) + Channel(g) + Channel(b);
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static string Channel(double value) =>
        ((int)Math.Floor(Math.Clamp(value, 0, 1) * 255 + 0.5)).ToString("X2", CultureInfo.InvariantCulture);
}
