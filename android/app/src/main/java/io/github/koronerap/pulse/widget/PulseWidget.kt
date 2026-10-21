package io.github.koronerap.pulse.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionStartActivity
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import io.github.koronerap.pulse.GameSummary
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.R
import io.github.koronerap.pulse.Snapshot
import io.github.koronerap.pulse.data.AnalyticsFailure
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.ui.MainActivity
import io.github.koronerap.pulse.work.RefreshWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * Ana ekran widget'ı: masaüstündeki şeridin karşılığı. Boyuta göre üç görünüm: küçükte
 * tek oyun, ortada üç, büyükte altı oyun; genişse istek listesinin günlük farkı da.
 * Dokununca uygulama açılıyor; sağ üstteki düğme hemen yeniliyor.
 */
class PulseWidget : GlanceAppWidget() {
    // LocalSize gerçek genişlik değil, bunlardan sığan en büyüğü: genişliğe göre değişen her şeyin bir boyutu olmalı.
    override val sizeMode = SizeMode.Responsive(setOf(SMALL, SMALL_MID, SMALL_WIDE, MEDIUM, MEDIUM_WIDE, LARGE, LARGE_WIDE))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val snapshot = withContext(Dispatchers.IO) { Pulse.get(context).snapshot() }
        provideContent { Content(snapshot) }
    }

    companion object {
        val SMALL = DpSize(110.dp, 48.dp)
        val SMALL_MID = DpSize(140.dp, 48.dp)
        val SMALL_WIDE = DpSize(200.dp, 48.dp)
        val MEDIUM = DpSize(250.dp, 110.dp)
        val MEDIUM_WIDE = DpSize(320.dp, 110.dp)
        val LARGE = DpSize(250.dp, 220.dp)
        val LARGE_WIDE = DpSize(320.dp, 220.dp)
    }
}

class PulseWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = PulseWidget()
}

/** Widget'taki yenile düğmesi. */
class RefreshAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        RefreshWorker.runNow(context)
    }
}

private val Background = Color(0xEB1E1E22)
private val Foreground = Color(0xFFF2F2F2)
private val Muted = Color(0xFFA0A0A8)
private val PlayersTone = Color(0xFF4C9AFF)
private val WishlistTone = Color(0xFFE06C9F)
private val SalesTone = Color(0xFF3FB950)
private val Danger = Color(0xFFE5534B)

private fun style(color: Color = Foreground, size: Int = 13, bold: Boolean = false) =
    TextStyle(color = ColorProvider(color), fontSize = size.sp, fontWeight = if (bold) FontWeight.Bold else FontWeight.Normal)

@Composable
private fun Content(snapshot: Snapshot) {
    val context = LocalContext.current
    val size = LocalSize.current
    val shown = snapshot.games.filter { it.game.showInWidget }.ifEmpty { snapshot.games }

    Box(
        modifier = GlanceModifier
            .fillMaxSize()
            .background(ColorProvider(Background))
            .cornerRadius(16.dp)
            .padding(horizontal = 12.dp, vertical = 8.dp)
            .clickable(actionStartActivity<MainActivity>()),
    ) {
        when {
            shown.isEmpty() -> Column(modifier = GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                Text("Pipe Manager Pulse", style = style(bold = true))
                Text(context.getString(R.string.widget_empty), style = style(Muted, 12))
            }

            size.height < PulseWidget.MEDIUM.height -> Single(
                shown.first(),
                metrics = when {
                    size.width >= PulseWidget.SMALL_WIDE.width -> 3
                    size.width >= PulseWidget.SMALL_MID.width -> 2
                    else -> 1
                },
            )

            else -> Column(modifier = GlanceModifier.fillMaxSize()) {
                Header(snapshot)
                Spacer(GlanceModifier.height(4.dp))

                val rows = if (size.height >= PulseWidget.LARGE.height) 6 else 3
                val wide = size.width >= PulseWidget.MEDIUM_WIDE.width

                for (summary in shown.take(rows)) {
                    GameRow(summary, wide)
                    Spacer(GlanceModifier.height(4.dp))
                }
            }
        }
    }
}

@Composable
private fun Header(snapshot: Snapshot) {
    val context = LocalContext.current
    val time = snapshot.lastRefreshAt?.atZone(ZoneId.systemDefault())?.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)).orEmpty()
    val rejected = AnalyticsFailure.InvalidKey in snapshot.failures.values

    Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Image(
            provider = ImageProvider(R.drawable.ic_trend),
            contentDescription = null,
            colorFilter = ColorFilter.tint(ColorProvider(PlayersTone)),
            modifier = GlanceModifier.size(16.dp),
        )
        Spacer(GlanceModifier.width(6.dp))
        Text(
            if (rejected) context.getString(R.string.widget_key_rejected) else time,
            style = style(if (rejected) Danger else Muted, 11),
            modifier = GlanceModifier.defaultWeight(),
        )
        Image(
            provider = ImageProvider(R.drawable.ic_refresh),
            contentDescription = context.getString(R.string.action_refresh),
            colorFilter = ColorFilter.tint(ColorProvider(Muted)),
            modifier = GlanceModifier.size(18.dp).clickable(actionRunCallback<RefreshAction>()),
        )
    }
}

/** Simge ve sayı, kendi satırında: Glance bir satırda en çok on öğe çiziyor, fazlasını atıyor. */
@Composable
private fun Metric(icon: Int, tone: Color, text: String, extra: String = "", extraTone: Color = Muted) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Image(
            provider = ImageProvider(icon),
            contentDescription = null,
            colorFilter = ColorFilter.tint(ColorProvider(tone)),
            modifier = GlanceModifier.size(13.dp),
        )
        Spacer(GlanceModifier.width(3.dp))
        Text(text, style = style(), maxLines = 1)

        if (extra.isNotEmpty()) {
            Spacer(GlanceModifier.width(3.dp))
            Text(extra, style = style(extraTone, 11), maxLines = 1)
        }
    }
}

@Composable
private fun GameRow(summary: GameSummary, wide: Boolean) {
    val analytics = summary.analytics

    Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(summary.game.title, style = style(bold = true), maxLines = 1, modifier = GlanceModifier.defaultWeight())
        Spacer(GlanceModifier.width(8.dp))
        Metric(R.drawable.ic_people, PlayersTone, PulseFormat.compact(analytics.players.current))

        analytics.wishlist?.let { wishlist ->
            Spacer(GlanceModifier.width(8.dp))
            val delta = wishlist.today.net
            Metric(
                R.drawable.ic_heart, WishlistTone, PulseFormat.compact(wishlist.total),
                if (delta == 0 || !wide) "" else PulseFormat.signed(delta),
                if (delta >= 0) SalesTone else Danger,
            )
        }

        analytics.sales?.let { sales ->
            Spacer(GlanceModifier.width(8.dp))
            Metric(R.drawable.ic_money, SalesTone, PulseFormat.compactUsd(sales.today.net))
        }
    }
}

/** Küçük widget: tek oyun, adı üstte, sayılar altta; dar olduğunda yalnızca ilk sayılar sığıyor. */
@Composable
private fun Single(summary: GameSummary, metrics: Int) {
    val analytics = summary.analytics

    val shown = buildList<@Composable () -> Unit> {
        add { Metric(R.drawable.ic_people, PlayersTone, PulseFormat.compact(analytics.players.current)) }
        analytics.wishlist?.let { add { Metric(R.drawable.ic_heart, WishlistTone, PulseFormat.compact(it.total)) } }
        analytics.sales?.let { add { Metric(R.drawable.ic_money, SalesTone, PulseFormat.compactUsd(it.today.net)) } }
    }.take(metrics)

    Column(modifier = GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
        Text(summary.game.title, style = style(bold = true, size = 12), maxLines = 1)
        Spacer(GlanceModifier.height(2.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            shown.forEachIndexed { index, metric ->
                if (index > 0) Spacer(GlanceModifier.width(8.dp))
                metric()
            }
        }
    }
}
