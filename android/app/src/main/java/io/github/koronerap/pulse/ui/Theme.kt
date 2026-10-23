package io.github.koronerap.pulse.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import io.github.koronerap.pulse.data.AppLook
import io.github.koronerap.pulse.data.LookTheme

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

/** Duvar kâğıdı renkleri (Material You) Android 12'den beri var. */
val wallpaperColorsAvailable: Boolean get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S

@Composable
fun PulseTheme(look: AppLook = AppLook(), content: @Composable () -> Unit) {
    val dark = when (look.theme) {
        LookTheme.Dark -> true
        LookTheme.Light -> false
        LookTheme.System -> isSystemInDarkTheme()
    }

    val context = LocalContext.current
    val scheme = when {
        look.wallpaperColors && wallpaperColorsAvailable -> if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> Dark
        else -> Light
    }

    MaterialTheme(colorScheme = scheme, content = content)
}
