package io.github.koronerap.pulse.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** Masaüstüyle aynı tonlar: oyuncu mavi, wishlist pembe, satış yeşil. */
object Tones {
    val Players = Color(0xFF4C9AFF)
    val Wishlist = Color(0xFFE06C9F)
    val Sales = Color(0xFF3FB950)
    val Danger = Color(0xFFE5534B)
    val Warning = Color(0xFFD9A93B)
}

// Material'ın varsayılan (eflatun) tonları yerine markanın mavisi: düğmeler, seçili çipler,
// açılır menüler aynı aileden.
private val Dark = darkColorScheme(
    primary = Tones.Players,
    onPrimary = Color.White,
    primaryContainer = Color(0xFF1D3A66),
    onPrimaryContainer = Color(0xFFD8E6FF),
    secondary = Color(0xFF9DB4D8),
    secondaryContainer = Color(0xFF2E3746),
    onSecondaryContainer = Color(0xFFDCE4F2),
    background = Color(0xFF1C1C1F),
    surface = Color(0xFF1C1C1F),
    surfaceVariant = Color(0xFF29292E),
    surfaceContainer = Color(0xFF242428),
    surfaceContainerHigh = Color(0xFF2E2E33),
    surfaceContainerHighest = Color(0xFF38383E),
)

private val Light = lightColorScheme(
    primary = Color(0xFF1F6FEB),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFD8E6FF),
    onPrimaryContainer = Color(0xFF0B2A5B),
    secondary = Color(0xFF4A5C78),
    secondaryContainer = Color(0xFFDCE4F2),
    onSecondaryContainer = Color(0xFF1A2333),
    background = Color(0xFFF7F8FA),
    surface = Color(0xFFF7F8FA),
    surfaceVariant = Color(0xFFE9EBF0),
    surfaceContainer = Color(0xFFEFF1F5),
    surfaceContainerHigh = Color(0xFFE9EBF0),
    surfaceContainerHighest = Color(0xFFE3E6EC),
)

@Composable
fun PulseTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) Dark else Light, content = content)
}
