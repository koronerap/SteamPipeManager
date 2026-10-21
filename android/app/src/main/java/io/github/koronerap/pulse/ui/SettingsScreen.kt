package io.github.koronerap.pulse.ui

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import io.github.koronerap.pulse.BuildConfig
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.Snapshot
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.data.StoredKey
import io.github.koronerap.pulse.widget.PulseWidgetReceiver
import io.github.koronerap.pulse.work.RefreshWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(pulse: Pulse, snapshot: Snapshot, onBack: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val prefs = pulse.preferences

    var currency by remember { mutableStateOf(prefs.currency) }
    var hide by remember { mutableStateOf(prefs.hideAmounts) }
    var minutes by remember { mutableStateOf(prefs.refreshMinutes) }
    var keys by remember { mutableStateOf(pulse.keys.all()) }
    var choosingCurrency by remember { mutableStateOf(false) }
    var removing by remember { mutableStateOf<StoredKey?>(null) }
    var clearing by remember { mutableStateOf(false) }

    fun changed() = scope.launch { pulse.changed() }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.settings_title)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.action_back)) } },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier.padding(padding).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            // Android 8+: başlatıcı onay sorup widget'ı ana ekrana koyuyor.
            val widgets = remember { AppWidgetManager.getInstance(context) }

            if (widgets.isRequestPinAppWidgetSupported) {
                Section(stringResource(R.string.settings_widget)) {
                    Text(stringResource(R.string.settings_widget_body), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedButton(onClick = {
                        widgets.requestPinAppWidget(ComponentName(context, PulseWidgetReceiver::class.java), null, null)
                    }) { Text(stringResource(R.string.settings_widget_add)) }
                }
            }

            Section(stringResource(R.string.settings_currency)) {
                val auto = stringResource(R.string.settings_currency_auto, PulseFormat.homeCurrency())
                val usdOnly = stringResource(R.string.settings_currency_usd)

                fun label(code: String?) = when (code) {
                    null -> auto
                    "USD" -> usdOnly
                    else -> "$code (${PulseFormat.symbolOf(code).trim()})"
                }

                Box {
                    OutlinedButton(onClick = { choosingCurrency = true }, modifier = Modifier.fillMaxWidth()) { Text(label(currency)) }

                    DropdownMenu(expanded = choosingCurrency, onDismissRequest = { choosingCurrency = false }) {
                        for (code in listOf<String?>(null) + PulseFormat.offeredCurrencies) {
                            DropdownMenuItem(text = { Text(label(code)) }, onClick = {
                                choosingCurrency = false
                                currency = code
                                prefs.currency = code
                                scope.launch { pulse.refresh(force = false) }
                            })
                        }
                    }
                }
            }

            Section(stringResource(R.string.settings_hide)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.settings_hide_body), modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                    Switch(checked = hide, onCheckedChange = {
                        hide = it
                        prefs.hideAmounts = it
                        changed()
                    })
                }
            }

            Section(stringResource(R.string.settings_interval)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for (option in listOf(15, 30, 60)) {
                        FilterChip(
                            selected = minutes == option,
                            onClick = {
                                minutes = option
                                prefs.refreshMinutes = option
                                RefreshWorker.schedule(context, option, replace = true)
                            },
                            label = { Text(pluralStringResource(R.plurals.settings_minutes, option, option)) },
                        )
                    }
                }
            }

            Section(stringResource(R.string.settings_keys)) {
                Text(stringResource(R.string.settings_keys_body), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

                if (keys.isEmpty()) {
                    Text(stringResource(R.string.settings_no_keys), style = MaterialTheme.typography.bodyMedium)
                }

                for (key in keys) {
                    val count = snapshot.games.count { it.game.keyId == key.id }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text("•••• ${key.hint}", style = MaterialTheme.typography.bodyLarge)
                            Text(pluralStringResource(R.plurals.settings_key_games, count, count), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        TextButton(onClick = { removing = key }) { Text(stringResource(R.string.remove_game)) }
                    }
                }
            }

            Section(stringResource(R.string.settings_clear)) {
                Text(stringResource(R.string.settings_clear_body), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedButton(onClick = { clearing = true }) { Text(stringResource(R.string.settings_clear)) }
            }

            Text(
                stringResource(R.string.settings_about, BuildConfig.VERSION_NAME),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    removing?.let { key ->
        Confirm(
            text = stringResource(R.string.settings_key_remove_confirm, key.hint),
            onConfirm = {
                removing = null
                scope.launch {
                    withContext(Dispatchers.IO) {
                        pulse.keys.remove(key.id)
                        pulse.games.save(pulse.games.load().map { if (it.keyId == key.id) it.copy(keyId = null) else it })
                    }
                    keys = pulse.keys.all()
                    pulse.changed()
                }
            },
            onDismiss = { removing = null },
        )
    }

    if (clearing) {
        Confirm(
            text = stringResource(R.string.settings_clear_body),
            onConfirm = {
                clearing = false
                scope.launch {
                    withContext(Dispatchers.IO) { pulse.store.clear() }
                    pulse.refresh(force = true)
                }
            },
            onDismiss = { clearing = false },
        )
    }
}

@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Column(modifier = Modifier.padding(16.dp).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            content()
        }
    }
}

@Composable
private fun Confirm(text: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        text = { Text(text) },
        confirmButton = { TextButton(onClick = onConfirm) { Text(stringResource(R.string.action_ok)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}
