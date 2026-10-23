package io.github.koronerap.pulse.widget

import android.content.Context
import android.os.Build
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
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
import io.github.koronerap.pulse.data.LookTheme
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.data.SalesPeriod
import io.github.koronerap.pulse.data.WidgetLayout
import io.github.koronerap.pulse.data.WidgetLook
import io.github.koronerap.pulse.ui.MainActivity
import io.github.koronerap.pulse.work.RefreshWorker
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import androidx.glance.color.ColorProvider as DayNight

/**
 * Ana ekran widget'ı: masaüstündeki şeridin karşılığı. Kısa olduğunda tek oyun, uzadıkça
 * daha çok oyun; genişse istek listesinin günlük farkı da. Görünümü (tema, saydamlık, yazı
 * boyutu, sayılar, sıra) uygulamanın ayarlarında. Dokununca uygulama açılıyor; sağ üstteki
 * düğme hemen yeniliyor.
 */
class PulseWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(SIZES)

    // Glance oturumu bir süre açık kalıyor; o sırada gelen güncellemeler provideGlance'i yeniden
    // çağırmıyor, yalnızca yeniden çiziyor. İçerik bu yüzden anlık görüntüyü izliyor: bir kez
    // okunsa açık oturumdaki değişiklikler (görünüm, yenileme) widget'a hiç ulaşmazdı.
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val pulse = Pulse.get(context)
        pulse.ensureLoaded()

        provideContent {
            val snapshot by pulse.snapshots.collectAsState()
            PulseContent(snapshot)
        }
    }

    companion object {
        // LocalSize gerçek boyut değil, bunlardan sığan en büyüğü: genişliğe göre değişen her
        // eşiğin bir basamağı olmalı (bkz. WidgetLayout). Android en çok 16 boyut kabul ediyor.
        private val WIDTHS = listOf(110, 170, 250, 330)
        private val HEIGHTS = listOf(48, 110, 220)

        val SIZES = HEIGHTS.flatMap { h -> WIDTHS.map { w -> DpSize(w.dp, h.dp) } }.toSet()
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

/** Temaya göre renkler; "sistemle aynı"da Android gece moduna göre kendisi seçiyor. */
private class Palette(
    val background: ColorProvider,
    val text: ColorProvider,
    val muted: ColorProvider,
    val players: ColorProvider,
    val wishlist: ColorProvider,
    val sales: ColorProvider,
    val danger: ColorProvider,
)

private fun palette(context: Context, look: WidgetLook): Palette {
    fun pick(dark: Color, light: Color): ColorProvider = when (look.theme) {
        LookTheme.Dark -> ColorProvider(dark)
        LookTheme.Light -> ColorProvider(light)
        LookTheme.System -> DayNight(day = light, night = dark)
    }

    // Duvar kâğıdı renkleri çizim anında okunuyor: duvar kâğıdı değişince bir sonraki yenilemede.
    val wallpaper = look.wallpaperColors && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S
    fun system(id: Int, fallback: Long) = if (wallpaper) Color(context.getColor(id)) else Color(fallback)

    val opacity = look.transparency.opacity

    return Palette(
        background = pick(
            system(android.R.color.system_neutral1_900, 0xFF1E1E22).copy(alpha = opacity),
            system(android.R.color.system_neutral1_50, 0xFFF7F7F9).copy(alpha = opacity),
        ),
        text = pick(system(android.R.color.system_neutral1_50, 0xFFF2F2F2), system(android.R.color.system_neutral1_900, 0xFF1B1B1F)),
        muted = pick(system(android.R.color.system_neutral2_200, 0xFFA0A0A8), system(android.R.color.system_neutral2_700, 0xFF5F5F68)),
        players = pick(system(android.R.color.system_accent1_200, 0xFF4C9AFF), system(android.R.color.system_accent1_600, 0xFF1F6FEB)),
        wishlist = pick(Color(0xFFE06C9F), Color(0xFFC2185B)),
        sales = pick(Color(0xFF3FB950), Color(0xFF1A7F37)),
        danger = pick(Color(0xFFE5534B), Color(0xFFC62828)),
    )
}

/** Çizim için gerekenler bir arada: görünüm, renkler, ölçek. */
private class Style(context: Context, val look: WidgetLook, val snapshot: Snapshot) {
    val colors = palette(context, look)

    /** Yazı ve simge ölçeği (dp). */
    val scale = look.textSize.scale

    /** Yerleşim eşikleri için: yazı ölçeği ile telefonun yazı boyutu birlikte. */
    val unit = scale * context.resources.configuration.fontScale

    fun text(color: ColorProvider = colors.text, size: Float = 13f, bold: Boolean = false) =
        TextStyle(color = color, fontSize = (size * scale).sp, fontWeight = if (bold) FontWeight.Bold else FontWeight.Normal)

    /** Seçilen dönemin satışı, istenirse kullanıcının para biriminde (kur varsa). */
    fun money(usd: Double): String {
        val rate = snapshot.rate
        return if (look.localCurrency && rate != null) PulseFormat.compactMoney(usd * rate, snapshot.currency) else PulseFormat.compactUsd(usd)
    }
}

/**
 * Widget'ın içeriği. Uygulamanın ayarlarındaki önizleme de bunu çiziyor; orada
 * [interactive] kapalı (dokunmalar widget'a ait).
 */
@Composable
fun PulseContent(snapshot: Snapshot, interactive: Boolean = true) {
    val context = LocalContext.current
    val size = LocalSize.current
    val style = Style(context, snapshot.look, snapshot)
    val look = snapshot.look
    val shown = look.ordered(snapshot.games.filter { it.game.showInWidget }.ifEmpty { snapshot.games }) { it.analytics }

    Box(
        modifier = GlanceModifier
            .fillMaxSize()
            .background(style.colors.background)
            .cornerRadius(16.dp)
            .padding(horizontal = 12.dp, vertical = 8.dp)
            .let { if (interactive) it.clickable(actionStartActivity<MainActivity>()) else it },
    ) {
        when {
            shown.isEmpty() -> Column(modifier = GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                Text("Pipe Manager Pulse", style = style.text(bold = true))
                Text(context.getString(R.string.widget_empty), style = style.text(style.colors.muted, 12f))
            }

            size.height.value < WidgetLayout.SINGLE_BELOW -> Single(shown.first(), style, WidgetLayout.singleMetrics(size.width.value, style.unit))

            else -> Column(modifier = GlanceModifier.fillMaxSize()) {
                if (look.showHeader) {
                    Header(style, interactive)
                    Spacer(GlanceModifier.height(4.dp))
                }

                val rows = WidgetLayout.rows(size.height.value, style.unit, look.showHeader)
                val metrics = WidgetLayout.rowMetrics(size.width.value, style.unit, look.showTitles)
                val wide = WidgetLayout.wide(size.width.value, style.unit)

                for (summary in shown.take(rows)) {
                    GameRow(summary, style, metrics, wide)
                    Spacer(GlanceModifier.height(4.dp))
                }
            }
        }
    }
}

@Composable
private fun Header(style: Style, interactive: Boolean) {
    val context = LocalContext.current
    val snapshot = style.snapshot
    val time = snapshot.lastRefreshAt?.atZone(ZoneId.systemDefault())?.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)).orEmpty()
    val rejected = AnalyticsFailure.InvalidKey in snapshot.failures.values

    // Satış bugünün değilse hangi dönemin olduğu başlıkta.
    val period = when (style.look.salesPeriod) {
        SalesPeriod.Today -> null
        SalesPeriod.Yesterday -> R.string.period_yesterday
        SalesPeriod.Week -> R.string.period_week
        SalesPeriod.Month -> R.string.period_month
    }?.takeIf { style.look.showSales }?.let { context.getString(it) }

    Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Image(
            provider = ImageProvider(R.drawable.ic_trend),
            contentDescription = null,
            colorFilter = ColorFilter.tint(style.colors.players),
            modifier = GlanceModifier.size((16 * style.scale).dp),
        )
        Spacer(GlanceModifier.width(6.dp))
        Text(
            if (rejected) context.getString(R.string.widget_key_rejected) else listOfNotNull(time.ifEmpty { null }, period).joinToString(" · "),
            style = style.text(if (rejected) style.colors.danger else style.colors.muted, 11f),
            maxLines = 1,
            modifier = GlanceModifier.defaultWeight(),
        )
        Image(
            provider = ImageProvider(R.drawable.ic_refresh),
            contentDescription = context.getString(R.string.action_refresh),
            colorFilter = ColorFilter.tint(style.colors.muted),
            modifier = GlanceModifier.size((18 * style.scale).dp).let { if (interactive) it.clickable(actionRunCallback<RefreshAction>()) else it },
        )
    }
}

/** Simge ve sayı, kendi satırında: Glance bir satırda en çok on öğe çiziyor, fazlasını atıyor. */
@Composable
private fun Metric(style: Style, icon: Int, tone: ColorProvider, text: String, extra: String = "", extraTone: ColorProvider = style.colors.muted) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Image(
            provider = ImageProvider(icon),
            contentDescription = null,
            colorFilter = ColorFilter.tint(tone),
            modifier = GlanceModifier.size((13 * style.scale).dp),
        )
        Spacer(GlanceModifier.width(3.dp))
        Text(text, style = style.text(), maxLines = 1)

        if (extra.isNotEmpty()) {
            Spacer(GlanceModifier.width(3.dp))
            Text(extra, style = style.text(extraTone, 11f), maxLines = 1)
        }
    }
}

/** Bir oyunun seçilen sayıları, sırayla; sığmayanlar sondan düşüyor. */
private fun metrics(summary: GameSummary, style: Style, wide: Boolean): List<@Composable () -> Unit> {
    val analytics = summary.analytics
    val look = style.look

    return buildList<@Composable () -> Unit> {
        if (look.showPlayers) {
            add { Metric(style, R.drawable.ic_people, style.colors.players, PulseFormat.compact(analytics.players.current)) }
        }

        if (look.showWishlists) {
            analytics.wishlist?.let { wishlist ->
                val delta = wishlist.today.net
                add {
                    Metric(
                        style, R.drawable.ic_heart, style.colors.wishlist, PulseFormat.compact(wishlist.total),
                        if (delta == 0 || !wide) "" else PulseFormat.signed(delta),
                        if (delta >= 0) style.colors.sales else style.colors.danger,
                    )
                }
            }
        }

        if (look.showSales) {
            analytics.sales?.let { sales ->
                add { Metric(style, R.drawable.ic_money, style.colors.sales, style.money(look.salesPeriod.of(sales).net)) }
            }
        }
    }
}

@Composable
private fun GameRow(summary: GameSummary, style: Style, metrics: Int, wide: Boolean) {
    Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        // Ad gizliyken de sayılar sağa yaslı: satırlar alt alta hizalı kalıyor.
        if (style.look.showTitles) {
            Text(summary.game.title, style = style.text(bold = true), maxLines = 1, modifier = GlanceModifier.defaultWeight())
        } else {
            Spacer(GlanceModifier.defaultWeight())
        }

        for (metric in metrics(summary, style, wide).take(metrics)) {
            Spacer(GlanceModifier.width(8.dp))
            metric()
        }
    }
}

/** Kısa widget: tek oyun, adı üstte, sayılar altta; dar olduğunda yalnızca ilk sayılar sığıyor. */
@Composable
private fun Single(summary: GameSummary, style: Style, metrics: Int) {
    Column(modifier = GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
        if (style.look.showTitles) {
            Text(summary.game.title, style = style.text(bold = true, size = 12f), maxLines = 1)
            Spacer(GlanceModifier.height(2.dp))
        }

        Row(verticalAlignment = Alignment.CenterVertically) {
            metrics(summary, style, wide = false).take(metrics).forEachIndexed { index, metric ->
                if (index > 0) Spacer(GlanceModifier.width(8.dp))
                metric()
            }
        }
    }
}
