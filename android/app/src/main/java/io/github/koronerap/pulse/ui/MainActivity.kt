package io.github.koronerap.pulse.ui

import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.data.Pairing
import kotlinx.coroutines.launch

/** Uygulamanın ekranları; az olduğu için basit bir durumla geçiliyor. */
sealed interface Screen {
    data object Home : Screen
    data object Add : Screen
    data object Settings : Screen
    data class Pair(val payload: String) : Screen
}

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { PulseTheme { PulseRoot() } }
    }
}

@Composable
private fun PulseRoot() {
    val context = LocalContext.current
    val pulse = remember { Pulse.get(context) }
    val snapshot by pulse.snapshots.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()

    var screen by remember { mutableStateOf<Screen>(Screen.Home) }
    var refreshing by remember { mutableStateOf(false) }

    fun refresh(force: Boolean) {
        if (refreshing) return
        refreshing = true

        scope.launch {
            try {
                pulse.refresh(force)
            } finally {
                refreshing = false
            }
        }
    }

    // Açılışta önbellekteki sayılar hemen (arka planda okunup), Steam'den yenileri arkasından.
    LaunchedEffect(Unit) {
        pulse.changed()
        refresh(force = false)
    }

    val scan = rememberLauncherForActivityResult(ScanContract()) { result ->
        val text = result.contents ?: return@rememberLauncherForActivityResult

        if (Pairing.looksLikePairing(text)) {
            screen = Screen.Pair(text)
        } else {
            Toast.makeText(context, R.string.pair_not_ours, Toast.LENGTH_LONG).show()
        }
    }

    fun startScan() {
        scan.launch(
            ScanOptions()
                .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt(context.getString(R.string.scan_prompt))
                .setBeepEnabled(false)
                .setOrientationLocked(false),
        )
    }

    BackHandler(enabled = screen != Screen.Home) { screen = Screen.Home }

    when (val current = screen) {
        Screen.Home -> HomeScreen(
            snapshot = snapshot,
            refreshing = refreshing,
            onRefresh = { refresh(force = true) },
            onAdd = { screen = Screen.Add },
            onSettings = { screen = Screen.Settings },
            onToggleWidget = { game ->
                scope.launch {
                    pulse.games.save(pulse.games.load().map { if (it.appId == game.appId) it.copy(showInWidget = !it.showInWidget) else it })
                    pulse.changed()
                }
            },
            onRemove = { game ->
                scope.launch {
                    pulse.games.save(pulse.games.load().filterNot { it.appId == game.appId })
                    pulse.changed()
                }
            },
        )

        Screen.Add -> AddScreen(
            pulse = pulse,
            onBack = { screen = Screen.Home },
            onScan = ::startScan,
            onPasted = { text ->
                if (Pairing.looksLikePairing(text)) {
                    screen = Screen.Pair(text)
                } else {
                    Toast.makeText(context, R.string.pair_not_ours, Toast.LENGTH_LONG).show()
                }
            },
            onAdded = {
                screen = Screen.Home
                refresh(force = true)
            },
        )

        is Screen.Pair -> PairScreen(
            pulse = pulse,
            payload = current.payload,
            onBack = { screen = Screen.Add },
            onImported = {
                Toast.makeText(context, R.string.pair_done, Toast.LENGTH_SHORT).show()
                screen = Screen.Home
                refresh(force = true)
            },
        )

        Screen.Settings -> SettingsScreen(
            pulse = pulse,
            snapshot = snapshot,
            onBack = { screen = Screen.Home },
        )
    }
}
