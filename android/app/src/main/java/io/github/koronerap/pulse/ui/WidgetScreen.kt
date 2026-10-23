package io.github.koronerap.pulse.ui

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.widget.FrameLayout
import android.widget.RemoteViews
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.glance.appwidget.ExperimentalGlanceRemoteViewsApi
import androidx.glance.appwidget.GlanceRemoteViews
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.Snapshot
import io.github.koronerap.pulse.data.GameOrder
import io.github.koronerap.pulse.data.LookTextSize
import io.github.koronerap.pulse.data.LookTheme
import io.github.koronerap.pulse.data.LookTransparency
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.data.SalesPeriod
import io.github.koronerap.pulse.data.WidgetLook
import io.github.koronerap.pulse.widget.PulseContent
import io.github.koronerap.pulse.widget.PulseWidgetReceiver
import kotlinx.coroutines.launch

/** Önizlemenin boyutları: yaygın ızgaralarda 2×1, 4×1 ve 4×2 hücre (dp). */
private enum class PreviewSize(val width: Int, val height: Int, @StringRes val label: Int) {
    Small(170, 70, R.string.preview_small),
    Wide(330, 70, R.string.preview_wide),
    Medium(330, 170, R.string.preview_medium),
}

/**
 * Widget'ın görünümü. Üstte widget'ın kendisi, seçilen boyutta, gerçek verilerle (ana
 * ekrandakinin aynısı çiziliyor); her değişiklik hem önizlemeye hem ana ekrana gidiyor.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WidgetScreen(pulse: Pulse, snapshot: Snapshot, onBack: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var look by remember { mutableStateOf(pulse.preferences.widgetLook) }
    var preview by remember { mutableStateOf(PreviewSize.Medium) }

    fun update(next: WidgetLook) {
        look = next
        scope.launch { pulse.setWidgetLook(next) }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.widget_title)) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.action_back)) } },
            )
        },
    ) { padding ->
        Column(modifier = Modifier.padding(padding).fillMaxSize()) {
            // Önizleme kaymıyor: ayarlar değiştikçe görünür kalıyor.
            Column(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                WidgetPreview(snapshot.copy(look = look), preview)

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for (size in PreviewSize.entries) {
                        FilterChip(selected = preview == size, onClick = { preview = size }, label = { Text(stringResource(size.label)) })
                    }
                }
            }

            HorizontalDivider()

            Column(
                modifier = Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                Choice(
                    stringResource(R.string.look_theme),
                    LookTheme.entries,
                    look.theme,
                    { stringResource(themeLabel(it)) },
                ) { update(look.copy(theme = it)) }

                if (wallpaperColorsAvailable) {
                    Toggle(stringResource(R.string.look_wallpaper), stringResource(R.string.look_wallpaper_body), look.wallpaperColors) {
                        update(look.copy(wallpaperColors = it))
                    }
                }

                Choice(
                    stringResource(R.string.look_transparency),
                    LookTransparency.entries,
                    look.transparency,
                    {
                        stringResource(
                            when (it) {
                                LookTransparency.None -> R.string.look_transparency_none
                                LookTransparency.Low -> R.string.look_transparency_low
                                LookTransparency.Medium -> R.string.look_transparency_medium
                                LookTransparency.High -> R.string.look_transparency_high
                            },
                        )
                    },
                ) { update(look.copy(transparency = it)) }

                Choice(
                    stringResource(R.string.look_text_size),
                    LookTextSize.entries,
                    look.textSize,
                    {
                        stringResource(
                            when (it) {
                                LookTextSize.Small -> R.string.look_text_small
                                LookTextSize.Normal -> R.string.look_text_normal
                                LookTextSize.Large -> R.string.look_text_large
                                LookTextSize.ExtraLarge -> R.string.look_text_extra_large
                            },
                        )
                    },
                ) { update(look.copy(textSize = it)) }

                Labeled(stringResource(R.string.look_show)) {
                    Chips {
                        Toggled(stringResource(R.string.look_show_titles), look.showTitles) { update(look.copy(showTitles = it)) }
                        Toggled(stringResource(R.string.look_show_players), look.showPlayers) { update(look.copy(showPlayers = it)) }
                        Toggled(stringResource(R.string.wishlists), look.showWishlists) { update(look.copy(showWishlists = it)) }
                        Toggled(stringResource(R.string.look_show_sales), look.showSales) { update(look.copy(showSales = it)) }
                    }
                }

                if (look.showSales) {
                    Choice(
                        stringResource(R.string.look_sales_period),
                        SalesPeriod.entries,
                        look.salesPeriod,
                        {
                            stringResource(
                                when (it) {
                                    SalesPeriod.Today -> R.string.period_today
                                    SalesPeriod.Yesterday -> R.string.period_yesterday
                                    SalesPeriod.Week -> R.string.period_week
                                    SalesPeriod.Month -> R.string.period_month
                                },
                            )
                        },
                    ) { update(look.copy(salesPeriod = it)) }

                    // Dolar dışında bir para birimi seçiliyse; kur yoksa widget yine dolar gösteriyor.
                    if (snapshot.currency != "USD") {
                        Toggle(
                            stringResource(R.string.look_local_currency, "${snapshot.currency} (${PulseFormat.symbolOf(snapshot.currency).trim()})"),
                            stringResource(R.string.look_local_currency_body),
                            look.localCurrency,
                        ) { update(look.copy(localCurrency = it)) }
                    }
                }

                Choice(
                    stringResource(R.string.look_order),
                    GameOrder.entries,
                    look.order,
                    {
                        stringResource(
                            when (it) {
                                GameOrder.Listed -> R.string.look_order_listed
                                GameOrder.Players -> R.string.look_order_players
                                GameOrder.Sales -> R.string.look_order_sales
                            },
                        )
                    },
                ) { update(look.copy(order = it)) }

                Toggle(stringResource(R.string.look_header), stringResource(R.string.look_header_body), look.showHeader) {
                    update(look.copy(showHeader = it))
                }

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    val widgets = remember { AppWidgetManager.getInstance(context) }

                    if (widgets.isRequestPinAppWidgetSupported) {
                        OutlinedButton(onClick = {
                            widgets.requestPinAppWidget(ComponentName(context, PulseWidgetReceiver::class.java), null, null)
                        }) { Text(stringResource(R.string.settings_widget_add)) }
                    }

                    TextButton(onClick = { update(WidgetLook()) }, enabled = look != WidgetLook()) {
                        Text(stringResource(R.string.look_reset))
                    }
                }
            }
        }
    }
}

fun themeLabel(theme: LookTheme): Int = when (theme) {
    LookTheme.Dark -> R.string.look_theme_dark
    LookTheme.Light -> R.string.look_theme_light
    LookTheme.System -> R.string.look_theme_system
}

/**
 * Widget'ın kendisi: ana ekrandaki gibi Glance'le çizilip burada gösteriliyor. Arkada bir
 * duvar kâğıdı geçişi: saydamlık ve tema görülebilsin.
 */
@OptIn(ExperimentalGlanceRemoteViewsApi::class)
@Composable
private fun WidgetPreview(snapshot: Snapshot, size: PreviewSize) {
    val context = LocalContext.current
    var views by remember { mutableStateOf<RemoteViews?>(null) }

    LaunchedEffect(snapshot, size) {
        views = GlanceRemoteViews()
            .compose(context, DpSize(size.width.dp, size.height.dp)) { PulseContent(snapshot, interactive = false) }
            .remoteViews
    }

    Box(
        modifier = Modifier
            .size(width = (size.width + 24).dp, height = (size.height + 24).dp)
            .clip(RoundedCornerShape(20.dp))
            .background(Brush.linearGradient(listOf(Color(0xFF2F5D8A), Color(0xFF7B4F9E), Color(0xFFD98E5F)))),
        contentAlignment = Alignment.Center,
    ) {
        views?.let { remote ->
            AndroidView(
                factory = { FrameLayout(it) },
                update = { frame ->
                    frame.removeAllViews()
                    frame.addView(remote.apply(frame.context, frame))
                },
                modifier = Modifier.size(size.width.dp, size.height.dp),
            )
        }
    }
}

@Composable
private fun Labeled(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(title, style = MaterialTheme.typography.titleSmall)
        content()
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun Chips(content: @Composable () -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) { content() }
}

/** Seçeneklerden biri: dar ekranda alt satıra geçen çipler. */
@Composable
fun <T> Choice(title: String, options: List<T>, selected: T, label: @Composable (T) -> String, onSelect: (T) -> Unit) {
    Labeled(title) {
        Chips {
            for (option in options) {
                FilterChip(selected = option == selected, onClick = { onSelect(option) }, label = { Text(label(option)) })
            }
        }
    }
}

@Composable
private fun Toggled(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    FilterChip(selected = checked, onClick = { onChange(!checked) }, label = { Text(label) })
}

/** Açıklamalı aç/kapa satırı; satırın tamamı dokunulabilir. */
@Composable
fun Toggle(title: String, body: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().toggleable(value = checked, onValueChange = onChange, role = Role.Switch),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f).padding(end = 12.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Text(body, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked = checked, onCheckedChange = null)
    }
}
