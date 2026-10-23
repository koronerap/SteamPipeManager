package io.github.koronerap.pulse.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.net.URLEncoder
import java.time.LocalDate

/** Steam'in herkese açık anlık oyuncu sayısı; anahtar gerekmiyor. */
class SteamPlayerCountClient(private val http: HttpGet) {
    /** Yayınlanmamış ya da verisi olmayan app'te null (Steam 404 / result 42 veriyor). */
    suspend fun currentPlayers(appId: Long): Int? {
        val json = SteamWebApi.getJson(
            http,
            "https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid=$appId",
            notFoundIsEmpty = true,
        ) ?: return null

        val body = SteamWebApi.body(json)
        return if (SteamWebApi.long(body, "result") == 1L) SteamWebApi.long(body, "player_count").toInt() else null
    }
}

/** Bir günün wishlist hareketleri (GMT günü). */
data class WishlistDay(val date: LocalDate, val adds: Int, val deletes: Int, val purchases: Int, val gifts: Int, val appMinDate: LocalDate?) {
    val net: Int get() = adds - deletes - purchases - gifts
}

/** Yeni ya da yeniden hesaplanmış satış verisi olan günler (Pasifik saati). */
data class ChangedDates(val dates: List<LocalDate>, val highwatermark: String)

/**
 * Steam'in gelir payı: ilk 10 milyon dolar için %70, 10–50 milyon arası %75, üstü %80.
 * Satış satırı o günün kademesini veriyor (0, 1, 2).
 */
object SteamRevenueShare {
    const val BASE = 0.70

    fun ofTier(tier: Int): Double = BASE + 0.05 * tier.coerceIn(0, 2)
}

/**
 * Satış toplamı. Net = brüt − iade − vergi; Steam'in payı düşülmemiş. [share] tahmini ortak
 * payı (net × o günün gelir payı); aylık rapordaki düzeltmeler dahil değil.
 */
@Serializable
data class SalesTotals(
    val units: Int = 0,
    val net: Double = 0.0,
    val gross: Double = 0.0,
    val returns: Double = 0.0,
    val tax: Double = 0.0,
    val share: Double = 0.0,
) {
    operator fun plus(other: SalesTotals) = SalesTotals(
        units + other.units, net + other.net, gross + other.gross,
        returns + other.returns, tax + other.tax, share + other.share,
    )

    val isZero: Boolean get() = this == ZERO

    companion object {
        val ZERO = SalesTotals()
    }
}

/** Bir günün satışları, app'e göre toplanmış. */
data class SalesDay(val date: LocalDate, val byApp: Map<Long, SalesTotals>, val appNames: Map<Long, String>)

/**
 * Steam'in yayıncıya özel finans servisi (IPartnerFinancialsService). Ayrı bir "Financial
 * API Group" anahtarı istiyor; anahtar ortağın **bütün** uygulamalarının satış ve wishlist
 * verisini görüyor, bu yüzden şifreli saklanıyor. Anahtar adreste gidiyor (Valve'ın
 * tarif ettiği yol); hata mesajları bu yüzden adres içermiyor.
 */
class SteamFinancialClient(private val http: HttpGet, private val host: String = DEFAULT_HOST) {
    /** Her istekten önce çağrılıyor; istek sayısı ve hızı buradan yönetiliyor. */
    var beforeRequest: (suspend () -> Unit)? = null

    private fun url(method: String, key: String, query: String) =
        "${host}IPartnerFinancialsService/$method/v001/?key=${URLEncoder.encode(key, "UTF-8")}&$query"

    private suspend fun get(url: String): JsonElement {
        beforeRequest?.invoke()
        return SteamWebApi.getJson(http, url)
            ?: throw AnalyticsException(AnalyticsFailure.BadResponse, "Steam returned no data.")
    }

    suspend fun wishlistDay(key: String, appId: Long, date: LocalDate): WishlistDay {
        val body = SteamWebApi.body(get(url("GetAppWishlistReporting", key, "appid=$appId&date=${SteamWebApi.dateText(date)}")))
        val summary = (body as? JsonObject)?.get("wishlist_summary") as? JsonObject

        fun count(name: String) = SteamWebApi.long(summary, name).toInt()

        return WishlistDay(
            date,
            count("wishlist_adds"),
            count("wishlist_deletes"),
            count("wishlist_purchases"),
            count("wishlist_gifts"),
            SteamWebApi.date(SteamWebApi.text(body, "app_min_date")),
        )
    }

    /** Son sorgudan bu yana verisi değişen günler; ilk çağrıda "0" verilir. */
    suspend fun changedDates(key: String, highwatermark: String): ChangedDates {
        val body = SteamWebApi.body(get(url("GetChangedDatesForPartner", key, "highwatermark=$highwatermark&$VIEW_GRANTS")))

        val dates = ((body as? JsonObject)?.get("dates") as? JsonArray).orEmpty()
            .mapNotNull { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.content }
            .mapNotNull(SteamWebApi::date)

        val next = (body as? JsonObject)?.get("result_highwatermark")?.let { (it as? JsonPrimitive)?.content }
        return ChangedDates(dates, next?.takeIf { it.isNotEmpty() && it != "0" } ?: highwatermark)
    }

    /**
     * Bir Pasifik günündeki satışlar, app'e göre toplanmış. Sayfalar Valve'ın tarifiyle
     * okunuyor: dönen max_id gönderilene eşit olana kadar. Paket satırları
     * primary_appid'ye, oyun içi satışlar kendi appid'sine yazılıyor; yalnızca
     * etkinleştirme olan satırlar (anahtar dağıtımı) gelir değil, sayılmıyor.
     */
    suspend fun salesDay(key: String, date: LocalDate): SalesDay {
        val byApp = mutableMapOf<Long, SalesTotals>()
        val names = mutableMapOf<Long, String>()
        var cursor = 0L

        repeat(MAX_PAGES) {
            val body = SteamWebApi.body(get(url("GetDetailedSales", key, "date=${SteamWebApi.dateText(date)}&highwatermark_id=$cursor&$VIEW_GRANTS")))
            val obj = body as? JsonObject

            for (row in (obj?.get("results") as? JsonArray).orEmpty()) {
                val (app, totals) = totalsOf(row)

                if (!totals.isZero) {
                    byApp[app] = (byApp[app] ?: SalesTotals.ZERO) + totals
                }
            }

            for (app in (obj?.get("app_info") as? JsonArray).orEmpty()) {
                val id = SteamWebApi.long(app, "appid")
                val name = SteamWebApi.text(app, "app_name")

                if (id > 0 && !name.isNullOrEmpty()) {
                    names[id] = name
                }
            }

            val maxId = SteamWebApi.long(body, "max_id")

            if (maxId <= cursor) {
                return SalesDay(date, byApp, names)
            }

            cursor = maxId
        }

        throw AnalyticsException(AnalyticsFailure.BadResponse, "Steam's sales data did not finish paging.")
    }

    private fun totalsOf(row: JsonElement): Pair<Long, SalesTotals> {
        val microTxn = SteamWebApi.text(row, "line_item_type").equals("MicroTxn", ignoreCase = true)
        val app = SteamWebApi.long(row, if (microTxn) "appid" else "primary_appid")
        val sold = SteamWebApi.long(row, "gross_units_sold").toInt()

        // İadeler raporda eksi işaretli gelebiliyor; burada hep pozitif.
        val returned = kotlin.math.abs(SteamWebApi.long(row, "gross_units_returned")).toInt()
        val units = if ((row as? JsonObject)?.containsKey("net_units_sold") == true) {
            SteamWebApi.long(row, "net_units_sold").toInt()
        } else {
            sold - returned
        }

        val net = SteamWebApi.money(row, "net_sales_usd")
        val tier = SteamWebApi.long(row, "additional_revenue_share_tier").toInt()

        return app to SalesTotals(
            units = units,
            net = net,
            gross = SteamWebApi.money(row, "gross_sales_usd"),
            returns = kotlin.math.abs(SteamWebApi.money(row, "gross_returns_usd")),
            tax = kotlin.math.abs(SteamWebApi.money(row, "net_tax_usd")),
            share = net * SteamRevenueShare.ofTier(tier),
        )
    }

    companion object {
        /** Yayıncı uçları için Valve'ın önerdiği adres. */
        const val DEFAULT_HOST = "https://partner.steam-api.com/"

        /** Sayfalamanın sonsuza dek sürmemesi için üst sınır. */
        private const val MAX_PAGES = 1000

        /**
         * Başka bir ortağın (çoğunlukla yayıncının) paylaştığı veri de istensin. Oyun yayıncının
         * hesabındaysa satışları orada; bu olmadan anahtar yalnızca kendi hesabındakileri
         * (örneğin dağıtılan anahtarları) görüyor ve satışlar hiç gelmiyor.
         */
        private const val VIEW_GRANTS = "include_view_grants=true"
    }
}

/** Mağazadan bir app'in adı (elle eklerken); anahtar gerekmiyor. Bulunamazsa null. */
class SteamStoreClient(private val http: HttpGet) {
    suspend fun appName(appId: Long): String? = try {
        val root = SteamWebApi.getJson(http, "https://store.steampowered.com/api/appdetails?appids=$appId&filters=basic") as? JsonObject
        val entry = root?.get(appId.toString()) as? JsonObject
        val data = entry?.get("data") as? JsonObject
        SteamWebApi.text(data, "name")
    } catch (_: AnalyticsException) {
        null
    }
}
