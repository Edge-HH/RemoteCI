package com.remoteci.mobile.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

private val LightBase = lightColorScheme(
    background = Color(0xFFFAF9FD),
    onBackground = Color(0xFF1B1B1F),
    surface = Color(0xFFFAF9FD),
    onSurface = Color(0xFF1B1B1F),
    surfaceVariant = Color(0xFFE3E2E6),
    onSurfaceVariant = Color(0xFF44474E),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF3F3FA),
    surfaceContainer = Color(0xFFEEEDF3),
    surfaceContainerHigh = Color(0xFFE9E8EF),
    surfaceContainerHighest = Color(0xFFE3E2E6),
    outline = Color(0xFF74777F),
    outlineVariant = Color(0xFFC4C6D0),
)

private val DarkBase = darkColorScheme(
    background = Color(0xFF131317),
    onBackground = Color(0xFFE2E2E8),
    surface = Color(0xFF131317),
    onSurface = Color(0xFFE2E2E8),
    surfaceVariant = Color(0xFF343439),
    onSurfaceVariant = Color(0xFFC6C5D2),
    surfaceContainerLowest = Color(0xFF0E0E12),
    surfaceContainerLow = Color(0xFF1B1B1F),
    surfaceContainer = Color(0xFF1F1F23),
    surfaceContainerHigh = Color(0xFF2A2A2E),
    surfaceContainerHighest = Color(0xFF343439),
    outline = Color(0xFF90909C),
    outlineVariant = Color(0xFF464651),
)

data class MobilePalette(
    val id: String,
    val label: String,
    val lightPrimary: Color,
    val lightContainer: Color,
    val lightOnContainer: Color,
    val darkPrimary: Color,
    val darkContainer: Color,
    val darkOnContainer: Color,
) {
    fun colorScheme(dark: Boolean): ColorScheme {
        val primary = if (dark) darkPrimary else lightPrimary
        val container = if (dark) darkContainer else lightContainer
        val onContainer = if (dark) darkOnContainer else lightOnContainer
        val onPrimary = if (dark) lightOnContainer else Color.White
        return (if (dark) DarkBase else LightBase).copy(
            primary = primary,
            onPrimary = onPrimary,
            primaryContainer = container,
            onPrimaryContainer = onContainer,
            secondary = primary,
            onSecondary = onPrimary,
            secondaryContainer = container,
            onSecondaryContainer = onContainer,
            tertiary = primary,
            onTertiary = onPrimary,
            tertiaryContainer = container,
            onTertiaryContainer = onContainer,
            inversePrimary = if (dark) lightPrimary else darkPrimary,
        )
    }

    companion object {
        val All = listOf(
            // 与 Wear OS 的 WatchPalette 共用同一组 M3 tonal palette，避免两端同名主题颜色不同。
            MobilePalette("lavender", "淡紫", Color(0xFF6750A4), Color(0xFFD8D4FF), Color(0xFF4A4459), Color(0xFFD8D4FF), Color(0xFFE8DEF8), Color(0xFF4A4459)),
            MobilePalette("purple", "经典紫", Color(0xFF6750A4), Color(0xFFEADDFF), Color(0xFF4F378B), Color(0xFFEADDFF), Color(0xFFE8DEF8), Color(0xFF4F378B)),
            MobilePalette("blue", "蓝色", Color(0xFF4660D9), Color(0xFFD8E2FF), Color(0xFF000E62), Color(0xFFD8E2FF), Color(0xFFE0E0FF), Color(0xFF000E62)),
            MobilePalette("green", "绿色", Color(0xFF416F43), Color(0xFFC3F0C2), Color(0xFF101F10), Color(0xFFC3F0C2), Color(0xFFD5E8D0), Color(0xFF101F10)),
            MobilePalette("orange", "橙色", Color(0xFF8B5000), Color(0xFFFFDBB8), Color(0xFF2A1707), Color(0xFFFFDBB8), Color(0xFFFFDBBD), Color(0xFF2A1707)),
            MobilePalette("pink", "粉色", Color(0xFF984061), Color(0xFFFFD8E2), Color(0xFF2B151B), Color(0xFFFFD8E2), Color(0xFFFFDAE3), Color(0xFF2B151B)),
        )

        fun fromId(id: String): MobilePalette = All.firstOrNull { it.id == id } ?: All.first()
    }
}

enum class AppearanceMode { System, Light, Dark }

fun AppearanceMode.resolve(systemDark: Boolean): Boolean = when (this) {
    AppearanceMode.System -> systemDark
    AppearanceMode.Light -> false
    AppearanceMode.Dark -> true
}

@Composable
fun RemoteCiTheme(
    appearanceMode: AppearanceMode = AppearanceMode.System,
    palette: MobilePalette = MobilePalette.fromId("lavender"),
    content: @Composable () -> Unit,
) {
    val dark = appearanceMode.resolve(isSystemInDarkTheme())
    val scheme = palette.colorScheme(dark)
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            view.context.findActivity()?.window?.let { window ->
                WindowCompat.getInsetsController(window, view).apply {
                    isAppearanceLightStatusBars = !dark
                    isAppearanceLightNavigationBars = !dark
                }
            }
        }
    }
    MaterialTheme(colorScheme = scheme) {
        Surface(modifier = Modifier.fillMaxSize(), color = scheme.background, content = content)
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}
