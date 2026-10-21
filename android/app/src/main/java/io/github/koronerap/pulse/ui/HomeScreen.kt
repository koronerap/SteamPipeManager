package io.github.koronerap.pulse.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.github.koronerap.pulse.GameSummary
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.Snapshot
import io.github.koronerap.pulse.data.AnalyticsFailure
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.data.TrackedGame
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    snapshot: Snapshot,
    refreshing: Boolean,
    onRefresh: () -> Unit,
    onAdd: () -> Unit,
    onSettings: () -> Unit,
    onToggleWidget: (TrackedGame) -> Unit,
    onRemove: (TrackedGame) -> Unit,
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name)) },
                actions = {
                    IconButton(onClick = onRefresh) { Icon(Icons.Default.Refresh, stringResource(R.string.action_refresh)) }
                    IconButton(onClick = onSettings) { Icon(Icons.Default.Settings, stringResource(R.string.action_settings)) }
                },
            )
        },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = onAdd,
                icon = { Icon(Icons.Default.Add, null) },
                text = { Text(stringResource(R.string.action_add)) },
            )
        },
    ) { padding ->
        val notice = notice(snapshot)

        PullToRefreshBox(isRefreshing = refreshing, onRefresh = onRefresh, modifier = Modifier.padding(padding).fillMaxSize()) {
            LazyColumn(
                contentPadding = PaddingValues(start = 16.dp, top = 4.dp, end = 16.dp, bottom = 96.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                notice?.let { (text, tone) ->
                    item { Notice(text, tone) }
                }

                snapshot.lastRefreshAt?.let { at ->
                    item {
                        val time = at.atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))
                        Text(
                            stringResource(R.string.updated, time),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }

                if (snapshot.games.isEmpty()) {
                    item { EmptyState() }
                }

                items(snapshot.games, key = { it.game.appId }) { summary ->
                    GameCard(summary, snapshot, onToggleWidget, onRemove)
                }
            }
        }
    }
}

@Composable
private fun notice(snapshot: Snapshot): Pair<String, Color>? {
    val failures = snapshot.failures.values

    return when {
        AnalyticsFailure.InvalidKey in failures -> stringResource(R.string.notice_key_rejected) to Tones.Danger
        AnalyticsFailure.RateLimited in failures -> stringResource(R.string.notice_rate_limited) to Tones.Warning
        failures.isNotEmpty() -> stringResource(R.string.notice_offline) to Tones.Warning
        snapshot.backfillPending -> stringResource(R.string.notice_backfilling) to Tones.Players
        else -> null
    }
}

@Composable
private fun Notice(text: String, tone: Color) {
    Card(colors = CardDefaults.cardColors(containerColor = tone.copy(alpha = 0.15f)), modifier = Modifier.fillMaxWidth()) {
        Text(text, modifier = Modifier.padding(14.dp), style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
private fun EmptyState() {
    Column(modifier = Modifier.fillMaxWidth().padding(vertical = 48.dp, horizontal = 8.dp)) {
        Text(stringResource(R.string.empty_title), style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(8.dp))
        Text(stringResource(R.string.empty_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun GameCard(summary: GameSummary, snapshot: Snapshot, onToggleWidget: (TrackedGame) -> Unit, onRemove: (TrackedGame) -> Unit) {
    val analytics = summary.analytics
    var menu by remember { mutableStateOf(false) }
    // Wishlist ve satış yoksa nedeni: anahtar yok, Steam reddetti ya da henüz inmedi.
    val placeholder = stringResource(
        when {
            !summary.hasKey -> R.string.needs_key
            snapshot.failures[summary.game.keyId] == AnalyticsFailure.InvalidKey -> R.string.widget_key_rejected
            else -> R.string.no_data_yet
        },
    )

    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row {
                Column(modifier = Modifier.weight(1f)) {
                    Text(summary.game.title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("App ${summary.game.appId}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }

                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, null) }

                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.show_in_widget)) },
                            leadingIcon = { if (summary.game.showInWidget) Icon(Icons.Default.Check, null) },
                            onClick = {
                                menu = false
                                onToggleWidget(summary.game)
                            },
                        )
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.remove_game)) },
                            leadingIcon = { Icon(Icons.Default.Delete, null) },
                            onClick = {
                                menu = false
                                onRemove(summary.game)
                            },
                        )
                    }
                }
            }

            Spacer(Modifier.height(12.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                val players = analytics.players
                Stat(
                    label = stringResource(R.string.players),
                    value = PulseFormat.count(players.current),
                    tone = Tones.Players,
                    details = listOf(
                        when {
                            !players.available -> stringResource(R.string.no_players)
                            players.peak24Hours != null -> stringResource(R.string.players_peak, PulseFormat.count(players.peak24Hours))
                            else -> ""
                        },
                    ),
                    modifier = Modifier.weight(1f),
                )

                val wishlist = analytics.wishlist
                Stat(
                    label = stringResource(R.string.wishlists),
                    value = wishlist?.let { PulseFormat.count(it.total) } ?: "—",
                    tone = Tones.Wishlist,
                    details = listOf(
                        when {
                            wishlist == null -> placeholder
                            !wishlist.isComplete -> stringResource(R.string.wishlist_incomplete)
                            else -> stringResource(R.string.wishlist_today, PulseFormat.signed(wishlist.today.net))
                        },
                    ),
                    modifier = Modifier.weight(1f),
                )

                val sales = analytics.sales
                Stat(
                    label = stringResource(R.string.sales_today),
                    value = sales?.let { PulseFormat.usd(it.today.net) } ?: "—",
                    tone = Tones.Sales,
                    details = if (sales == null) {
                        listOf(placeholder)
                    } else {
                        listOf(
                            stringResource(R.string.sales_units, PulseFormat.count(sales.today.units)),
                            PulseFormat.local(snapshot.rate?.let { sales.today.net * it }, snapshot.currency.takeIf { snapshot.rate != null }),
                            stringResource(R.string.sales_payout, PulseFormat.usd(sales.today.share)),
                            stringResource(R.string.sales_month) + ": " + PulseFormat.usd(sales.last30Days.net),
                        )
                    },
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

@Composable
private fun Stat(label: String, value: String, tone: Color, details: List<String>, modifier: Modifier = Modifier) {
    Column(modifier = modifier) {
        Text(label, style = MaterialTheme.typography.labelSmall, color = tone)
        Text(value, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 1)

        for (detail in details.filter { it.isNotEmpty() }) {
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
