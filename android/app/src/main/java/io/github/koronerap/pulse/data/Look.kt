package io.github.koronerap.pulse.data

import kotlinx.serialization.Serializable
import kotlin.math.floor

/** Koyu, açık ya da telefonun ayarı; masaüstündeki widget temasının karşılığı. */
@Serializable
enum class LookTheme { Dark, Light, System }

/** Metin boyutu; simgeler ve boşluklar da aynı ölçekle. Masaüstüyle aynı oranlar. */
@Serializable
enum class LookTextSize(val scale: Float) { Small(0.85f), Normal(1f), Large(1.2f), ExtraLarge(1.45f) }

/** Arka planın saydamlığı; en saydamında bile yazılar her duvar kâğıdında okunuyor. */
@Serializable
enum class LookTransparency(val opacity: Float) { None(1f), Low(0.92f), Medium(0.7f), High(0.45f) }

/** Widget'ta hangi dönemin satışı. */
@Serializable
enum class SalesPeriod {
    Today, Yesterday, Week, Month;

    fun of(stats: SalesStats): SalesTotals = when (this) {
        Today -> stats.today
        Yesterday -> stats.yesterday
        Week -> stats.last7Days
        Month -> stats.last30Days
    }
}

/** Widget'ta oyunların sırası. */
@Serializable
enum class GameOrder { Listed, Players, Sales }

/**
 * Ana ekran widget'ının görünümü. Bütün widget'lar aynısını kullanıyor; hangi oyunların
 * göründüğü oyunun kendi menüsünde.
 */
@Serializable
data class WidgetLook(
    val theme: LookTheme = LookTheme.Dark,
    /** Android 12+: renkler duvar kâğıdından; tema yine koyu, açık ya da sistemle aynı. */
    val wallpaperColors: Boolean = false,
    val transparency: LookTransparency = LookTransparency.Low,
    val textSize: LookTextSize = LookTextSize.Normal,
    val showTitles: Boolean = true,
    val showPlayers: Boolean = true,
    val showWishlists: Boolean = true,
    val showSales: Boolean = true,
    val salesPeriod: SalesPeriod = SalesPeriod.Today,
    /** Tutarlar seçilen para biriminde (kur varsa); kapalıyken dolar. */
    val localCurrency: Boolean = false,
    /** Üstte güncelleme saati ve yenile düğmesi. */
    val showHeader: Boolean = true,
    val order: GameOrder = GameOrder.Listed,
) {
    /**
     * Oyunlar seçilen sırayla. Eşitlikte ve "eklendiği gibi"de listedeki yeri korunuyor;
     * verisi olmayan oyun sona.
     */
    fun <T> ordered(games: List<T>, analytics: (T) -> AppAnalytics): List<T> = when (order) {
        GameOrder.Listed -> games
        GameOrder.Players -> games.sortedByDescending { analytics(it).players.current ?: -1 }
        GameOrder.Sales -> games.sortedByDescending { analytics(it).sales?.let { s -> salesPeriod.of(s).net } ?: -1.0 }
    }
}

/** Uygulamanın kendi görünümü. */
@Serializable
data class AppLook(
    val theme: LookTheme = LookTheme.System,
    /** Android 12+: Material You, renkler duvar kâğıdından. */
    val wallpaperColors: Boolean = false,
)

/**
 * Widget'ın ölçüye göre yerleşimi. Glance'te ölçü (LocalSize) gerçek boyut değil, tanımlı
 * boyutlardan sığan en büyüğü; eşikler o boyutlarla karşılaştırılıyor. [unit] metin boyutu
 * ile telefonun yazı ölçeğinin çarpımı: büyük yazıda aynı genişliğe daha az şey sığıyor.
 */
object WidgetLayout {
    /** Başlık satırının yüksekliği (dp, ölçek 1). */
    private const val HEADER = 22f

    /** Bir oyun satırı: 13 sp yazı, satır aralığıyla, ve satırlar arası boşluk (dp, ölçek 1). */
    private const val ROW = 19.5f + 4f

    /** Dikey iç boşluk, üst ve alt (dp). */
    private const val PADDING = 16f

    /** Tek oyunluk (tek satırlık) görünüm bu yükseklikten kısaysa. */
    const val SINGLE_BELOW = 110f

    /** Bu yükseklikte kaç oyun satırı sığıyor; en az bir. */
    fun rows(height: Float, unit: Float, header: Boolean): Int {
        val available = height - PADDING - if (header) HEADER * unit else 0f
        return floor(available / (ROW * unit)).toInt().coerceAtLeast(1)
    }

    /** Wishlist'in günlük farkı da sığıyor mu. */
    fun wide(width: Float, unit: Float): Boolean = width >= 320f * unit

    /**
     * Tek oyunluk görünümde kaç sayı sığıyor (1–3). Bir sayı simgesi ve boşluğuyla ~50 dp,
     * aralarda 8 dp, yanlarda 24 dp iç boşluk.
     */
    fun singleMetrics(width: Float, unit: Float): Int = when {
        width >= 180f * unit -> 3
        width >= 130f * unit -> 2
        else -> 1
    }

    /** Bir oyun satırında adın yanına kaç sayı sığıyor; ad için ~60 dp ayrılıyor. */
    fun rowMetrics(width: Float, unit: Float, titles: Boolean): Int =
        singleMetrics(if (titles) width - 60f * unit else width, unit)
}
