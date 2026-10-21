package io.github.koronerap.pulse.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.data.TrackedGame
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Anahtar seçimi: yok, kayıtlı bir anahtar ya da yeni. */
private sealed interface KeyChoice {
    data object None : KeyChoice
    data object New : KeyChoice
    data class Existing(val id: String, val hint: String) : KeyChoice
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddScreen(pulse: Pulse, onBack: () -> Unit, onScan: () -> Unit, onPasted: (String) -> Unit, onAdded: () -> Unit) {
    val scope = rememberCoroutineScope()
    val storedKeys = remember { pulse.keys.all() }

    var appId by remember { mutableStateOf("") }
    var choice by remember { mutableStateOf<KeyChoice>(storedKeys.firstOrNull()?.let { KeyChoice.Existing(it.id, it.hint) } ?: KeyChoice.None) }
    var newKey by remember { mutableStateOf("") }
    var choosing by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var pasting by remember { mutableStateOf(false) }
    val invalid = stringResource(R.string.manual_invalid)

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.add_title)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.action_back)) } },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier.padding(padding).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            // Masaüstünden QR: oyunlar ve anahtarlar birlikte geliyor.
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.pair_scan), style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(R.string.pair_scan_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Button(onClick = onScan, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.pair_scan)) }
                    TextButton(onClick = { pasting = true }) { Text(stringResource(R.string.pair_paste)) }
                }
            }

            // Elle: App ID ve isteğe bağlı finans anahtarı.
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(stringResource(R.string.manual_title), style = MaterialTheme.typography.titleMedium)

                    OutlinedTextField(
                        value = appId,
                        onValueChange = { appId = it.filter(Char::isDigit).take(10) },
                        label = { Text(stringResource(R.string.manual_app_id)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )

                    Text(stringResource(R.string.manual_key), style = MaterialTheme.typography.labelMedium)

                    Box {
                        OutlinedButton(onClick = { choosing = true }, modifier = Modifier.fillMaxWidth()) {
                            Text(
                                when (val c = choice) {
                                    KeyChoice.None -> stringResource(R.string.manual_key_none)
                                    KeyChoice.New -> stringResource(R.string.manual_key_new)
                                    is KeyChoice.Existing -> "•••• ${c.hint}"
                                },
                            )
                        }

                        DropdownMenu(expanded = choosing, onDismissRequest = { choosing = false }) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.manual_key_none)) }, onClick = { choice = KeyChoice.None; choosing = false })

                            for (key in storedKeys) {
                                DropdownMenuItem(text = { Text("•••• ${key.hint}") }, onClick = { choice = KeyChoice.Existing(key.id, key.hint); choosing = false })
                            }

                            DropdownMenuItem(text = { Text(stringResource(R.string.manual_key_new)) }, onClick = { choice = KeyChoice.New; choosing = false })
                        }
                    }

                    if (choice == KeyChoice.New) {
                        OutlinedTextField(
                            value = newKey,
                            onValueChange = { newKey = it.trim() },
                            label = { Text(stringResource(R.string.manual_key_input)) },
                            visualTransformation = PasswordVisualTransformation(),
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }

                    error?.let { Text(it, color = Tones.Danger, style = MaterialTheme.typography.bodySmall) }

                    Button(
                        enabled = !busy,
                        modifier = Modifier.fillMaxWidth(),
                        onClick = {
                            val id = appId.toLongOrNull()

                            if (id == null || id <= 0) {
                                error = invalid
                                return@Button
                            }

                            busy = true
                            error = null

                            scope.launch {
                                val keyId = when (val c = choice) {
                                    KeyChoice.None -> null
                                    is KeyChoice.Existing -> c.id
                                    KeyChoice.New -> newKey.takeIf { it.isNotEmpty() }?.let(pulse.keys::add)
                                }

                                // Ad mağazadan; bulunamazsa numarası.
                                val title = pulse.storeClient.appName(id) ?: "App $id"

                                withContext(Dispatchers.IO) { pulse.games.merge(listOf(TrackedGame(id, title, keyId))) }
                                pulse.changed()
                                busy = false
                                onAdded()
                            }
                        },
                    ) { Text(stringResource(R.string.manual_add)) }
                }
            }
        }
    }

    if (pasting) {
        var text by remember { mutableStateOf("") }

        AlertDialog(
            onDismissRequest = { pasting = false },
            title = { Text(stringResource(R.string.pair_paste)) },
            text = {
                OutlinedTextField(value = text, onValueChange = { text = it }, placeholder = { Text("SPMPULSE1.…") }, modifier = Modifier.fillMaxWidth())
            },
            confirmButton = {
                TextButton(onClick = {
                    pasting = false
                    onPasted(text.trim())
                }) { Text(stringResource(R.string.pair_continue)) }
            },
            dismissButton = { TextButton(onClick = { pasting = false }) { Text(stringResource(R.string.action_cancel)) } },
        )
    }
}
