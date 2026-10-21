package io.github.koronerap.pulse.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.data.Pairing
import io.github.koronerap.pulse.data.PairingData
import io.github.koronerap.pulse.data.TrackedGame
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * QR'dan gelen veri: önce masaüstünde görünen 8 haneli kod (veri onunla şifreli), sonra
 * ne geleceğinin özeti ve içe aktarma. Aynı oyun zaten varsa adı ve anahtarı güncelleniyor.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairScreen(pulse: Pulse, payload: String, onBack: () -> Unit, onImported: () -> Unit) {
    val scope = rememberCoroutineScope()

    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var wrong by remember { mutableStateOf(false) }
    var data by remember { mutableStateOf<PairingData?>(null) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.pair_scan)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.action_back)) } },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier.padding(padding).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            val decoded = data

            if (decoded == null) {
                Text(stringResource(R.string.pair_code_title), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.pair_code_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)

                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it.filter(Char::isDigit).take(8); wrong = false },
                    label = { Text(stringResource(R.string.pair_code)) },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    singleLine = true,
                    isError = wrong,
                    modifier = Modifier.fillMaxWidth(),
                )

                if (wrong) {
                    Text(stringResource(R.string.pair_wrong_code), color = Tones.Danger, style = MaterialTheme.typography.bodySmall)
                }

                Button(
                    enabled = code.length == 8 && !busy,
                    modifier = Modifier.fillMaxWidth(),
                    onClick = {
                        busy = true

                        scope.launch {
                            // PBKDF2 kasıtlı olarak yavaş: arayüzü bekletmesin.
                            val result = withContext(Dispatchers.Default) { Pairing.decode(payload, code) }
                            busy = false
                            wrong = result == null
                            data = result
                        }
                    },
                ) {
                    if (busy) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                            Text(stringResource(R.string.pair_decrypting))
                        }
                    } else {
                        Text(stringResource(R.string.pair_unlock))
                    }
                }
            } else {
                Text(
                    pluralStringResource(R.plurals.pair_games, decoded.games.size, decoded.games.size) + " · " +
                        pluralStringResource(R.plurals.pair_keys, decoded.keys.size, decoded.keys.size),
                    style = MaterialTheme.typography.titleMedium,
                )

                for (game in decoded.games) {
                    Text("• ${game.title}", style = MaterialTheme.typography.bodyMedium)
                }

                Button(
                    enabled = !busy,
                    modifier = Modifier.fillMaxWidth(),
                    onClick = {
                        busy = true

                        scope.launch {
                            withContext(Dispatchers.IO) { import(pulse, decoded) }
                            pulse.changed()
                            busy = false
                            onImported()
                        }
                    },
                ) { Text(stringResource(R.string.pair_import)) }
            }
        }
    }
}

/** Anahtarlar şifreli depoya, oyunlar listeye; para birimi masaüstünde seçildiyse o da. */
private fun import(pulse: Pulse, data: PairingData) {
    val keyIds = data.keys.map(pulse.keys::add)

    pulse.games.merge(data.games.map { TrackedGame(it.id, it.title, it.key?.let(keyIds::getOrNull)) })

    if (data.currency != null && pulse.preferences.currency == null) {
        pulse.preferences.currency = data.currency
    }
}
