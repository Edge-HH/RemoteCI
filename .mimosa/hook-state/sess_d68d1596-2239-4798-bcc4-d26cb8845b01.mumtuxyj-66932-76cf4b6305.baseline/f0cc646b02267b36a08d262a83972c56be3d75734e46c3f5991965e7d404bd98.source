package com.remoteci.watch.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.wear.compose.material.MaterialTheme

/**
 * Wear OS 应用的 Material Design 3 风格配色。
 *
 * 所有色值取自 M3 官方色板（tonal palette），同一方案内保证
 * 容器色/前景色对比度一致。切换主题即切换整套配色。
 */
data class WatchPalette(
    val id: String,
    val label: String,
    /** 首页状态页背景色。 */
    val homePanel: Color,
    /** 高亮容器色（分页指示器、选中页等）。 */
    val primaryContainer: Color,
    /** 课表卡片等次要容器色。 */
    val secondaryContainer: Color,
    /** 按钮/交互底色（M3 secondaryContainer 语义）。 */
    val buttonContainer: Color,
    /** 按钮上的文字与图标色（M3 onSecondaryContainer 语义）。 */
    val onButtonContainer: Color,
    /** 进度环激活色（M3 primary 语义）。 */
    val progressActive: Color,
    /** 进度环轨道色。 */
    val progressTrack: Color,
    /** 禁用态底色。 */
    val disabledContainer: Color,
    /** 禁用态前景色。 */
    val disabledContent: Color,
) {
    companion object {
        /** 内置的 M3 配色方案，顺序即外观设置页展示顺序。 */
        val All = listOf(Lavender, Purple, Blue, Green, Orange, Pink)

        /** 按持久化的 id 查找配色，未知 id 回退到默认淡紫。 */
        fun fromId(id: String): WatchPalette =
            All.firstOrNull { it.id == id } ?: Lavender
    }
}

/** 默认淡紫方案：对齐 Figma 深色圆形画布与 Material 3 淡紫容器色。 */
private val Lavender = WatchPalette(
    id = "lavender",
    label = "淡紫",
    homePanel = Color(0xFFF4E7FF),
    primaryContainer = Color(0xFFD8D4FF),
    secondaryContainer = Color(0xFFFFE1FA),
    buttonContainer = Color(0xFFE8DEF8),
    onButtonContainer = Color(0xFF4A4459),
    progressActive = Color(0xFF6750A4),
    progressTrack = Color(0xFFEEE5FA),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 经典紫：M3 默认 primary #6750A4 系列。 */
private val Purple = WatchPalette(
    id = "purple",
    label = "经典紫",
    homePanel = Color(0xFFF3EDF7),
    primaryContainer = Color(0xFFEADDFF),
    secondaryContainer = Color(0xFFE8DEF8),
    buttonContainer = Color(0xFFE8DEF8),
    onButtonContainer = Color(0xFF4F378B),
    progressActive = Color(0xFF6750A4),
    progressTrack = Color(0xFFE6E0E9),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 蓝色：M3 blue 官方色板。 */
private val Blue = WatchPalette(
    id = "blue",
    label = "蓝色",
    homePanel = Color(0xFFE4EBFA),
    primaryContainer = Color(0xFFD8E2FF),
    secondaryContainer = Color(0xFFE0E0FF),
    buttonContainer = Color(0xFFE0E0FF),
    onButtonContainer = Color(0xFF000E62),
    progressActive = Color(0xFF4660D9),
    progressTrack = Color(0xFFE1E7F5),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 绿色：M3 green 官方色板。 */
private val Green = WatchPalette(
    id = "green",
    label = "绿色",
    homePanel = Color(0xFFE7F2E3),
    primaryContainer = Color(0xFFC3F0C2),
    secondaryContainer = Color(0xFFD5E8D0),
    buttonContainer = Color(0xFFD5E8D0),
    onButtonContainer = Color(0xFF101F10),
    progressActive = Color(0xFF416F43),
    progressTrack = Color(0xFFDFE5DA),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 橙色：M3 orange 官方色板。 */
private val Orange = WatchPalette(
    id = "orange",
    label = "橙色",
    homePanel = Color(0xFFF8EADF),
    primaryContainer = Color(0xFFFFDBB8),
    secondaryContainer = Color(0xFFFFDBBD),
    buttonContainer = Color(0xFFFFDBBD),
    onButtonContainer = Color(0xFF2A1707),
    progressActive = Color(0xFF8B5000),
    progressTrack = Color(0xFFF2E1D7),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 粉色：M3 pink 官方色板。 */
private val Pink = WatchPalette(
    id = "pink",
    label = "粉色",
    homePanel = Color(0xFFF7E4EA),
    primaryContainer = Color(0xFFFFD8E2),
    secondaryContainer = Color(0xFFFFDAE3),
    buttonContainer = Color(0xFFFFDAE3),
    onButtonContainer = Color(0xFF2B151B),
    progressActive = Color(0xFF984061),
    progressTrack = Color(0xFFF1DDE2),
    disabledContainer = Color(0xFF343238),
    disabledContent = Color(0xFF8E8A94),
)

/** 当前配色，由 [AppTheme] 提供，屏幕组件统一从这里取色。 */
val LocalWatchPalette = staticCompositionLocalOf { Lavender }

/** Wear OS 应用主题：以选中的 M3 配色驱动 Material 主题与全屏取色。 */
@Composable
fun AppTheme(
    palette: WatchPalette = WatchPalette.fromId("lavender"),
    content: @Composable () -> Unit,
) {
    val colors = MaterialTheme.colors.copy(
        primary = palette.buttonContainer,
        primaryVariant = palette.progressActive,
        secondary = palette.secondaryContainer,
        secondaryVariant = palette.primaryContainer,
        background = Color(0xFF1D1D1D),
        surface = Color(0xFF1D1D1D),
        onPrimary = palette.onButtonContainer,
        onSecondary = Color.Black,
        onBackground = Color.White,
        onSurface = Color.White,
    )
    CompositionLocalProvider(LocalWatchPalette provides palette) {
        MaterialTheme(colors = colors, content = content)
    }
}
