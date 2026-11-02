package com.remoteci.mobile.ui

import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

/**
 * 学科颜色：按去首尾空白的学科名对应，用户改过的颜色按账号保存在服务端（见 SubjectColorStore）。
 * 本对象是服务端 RemoteCI.Shared.Models.SubjectColors 的逐行移植，未改过的学科在手机与 WebUI 上必须算出同一种颜色，
 * 两端共用 shared/test-fixtures/subject-colors.json 校验。
 */
object SubjectColors {
    /** 可选的 18 种基准色；色块的底色与文字色由 [derive] 按浅色/深色模式派生。 */
    val palette = listOf(
        "#6F9FE0", "#E58C8C", "#9B8BDB", "#66BFA0", "#E9A55F", "#5FB8CC", "#DC8FC2", "#9CC06A", "#E3967A",
        "#8297DA", "#C59E76", "#72B3E6", "#B48AD8", "#E2BC57", "#78C495", "#D9879E", "#86A9B5", "#C79BDF",
    )

    /** 常见学科的固定颜色（色板下标）；顺序也是名称包含匹配的优先顺序。 */
    private val builtin = listOf(
        "语文" to 1, "数学" to 0, "英语" to 2, "物理" to 4, "化学" to 3, "生物" to 7,
        "道德与法治" to 15, "思想政治" to 15, "政治" to 15, "道法" to 15, "历史" to 10, "地理" to 5,
        "信息" to 11, "通用技术" to 16, "体育" to 14, "音乐" to 6, "美术" to 8, "心理" to 17,
        "班会" to 13, "自习" to 9, "劳动" to 12, "日语" to 12, "科学" to 3, "书法" to 10,
    )

    fun key(subject: String?): String = subject?.trim().orEmpty()

    fun default(subject: String?): String = palette[defaultSwatch(subject)]

    fun defaultSwatch(subject: String?): Int {
        val name = key(subject)
        builtin.firstOrNull { it.first == name }?.let { return it.second }
        builtin.firstOrNull { name.contains(it.first) }?.let { return it.second }
        return (fnv1a(name) % palette.size.toUInt()).toInt()
    }

    /** 规范为色板中的写法；不在色板中返回 null。 */
    fun normalizePaletteColor(color: String?): String? =
        color?.trim()?.let { value -> palette.firstOrNull { it.equals(value, ignoreCase = true) } }

    /** 由基准色派生底色与文字色（#RRGGBB），保持色相；浅色模式浅底深字，深色模式暗底浅字。 */
    fun derive(baseColor: String, dark: Boolean): Pair<String, String> {
        val (h, s, _) = toHsl(baseColor)
        return if (dark) fromHsl(h, s * 0.45, 0.24) to fromHsl(h, min(s * 0.8, 0.75), 0.84)
        else fromHsl(h, min(s * 0.9, 0.7), 0.92) to fromHsl(h, min(s, 0.5), 0.3)
    }

    private fun fnv1a(text: String): UInt {
        var hash = 2166136261u
        for (byte in text.toByteArray(Charsets.UTF_8)) {
            hash = hash xor byte.toUByte().toUInt()
            hash *= 16777619u
        }
        return hash
    }

    private fun toHsl(hex: String): Triple<Double, Double, Double> {
        val value = hex.trimStart('#').toInt(16)
        val r = ((value shr 16) and 0xFF) / 255.0
        val g = ((value shr 8) and 0xFF) / 255.0
        val b = (value and 0xFF) / 255.0
        val maxValue = max(r, max(g, b))
        val minValue = min(r, min(g, b))
        val l = (maxValue + minValue) / 2
        if (maxValue == minValue) return Triple(0.0, 0.0, l)
        val d = maxValue - minValue
        val s = if (l > 0.5) d / (2 - maxValue - minValue) else d / (maxValue + minValue)
        val h = when (maxValue) {
            r -> (g - b) / d + (if (g < b) 6 else 0)
            g -> (b - r) / d + 2
            else -> (r - g) / d + 4
        }
        return Triple(h / 6, s, l)
    }

    private fun fromHsl(h: Double, s: Double, l: Double): String {
        val (r, g, b) = if (s == 0.0) Triple(l, l, l) else {
            val q = if (l < 0.5) l * (1 + s) else l + s - l * s
            val p = 2 * l - q
            Triple(hue(p, q, h + 1.0 / 3), hue(p, q, h), hue(p, q, h - 1.0 / 3))
        }
        return "#" + channel(r) + channel(g) + channel(b)
    }

    private fun hue(p: Double, q: Double, value: Double): Double {
        var t = value
        if (t < 0) t += 1
        if (t > 1) t -= 1
        if (t < 1.0 / 6) return p + (q - p) * 6 * t
        if (t < 1.0 / 2) return q
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6
        return p
    }

    private fun channel(value: Double): String =
        floor(value.coerceIn(0.0, 1.0) * 255 + 0.5).toInt().toString(16).padStart(2, '0').uppercase()
}
