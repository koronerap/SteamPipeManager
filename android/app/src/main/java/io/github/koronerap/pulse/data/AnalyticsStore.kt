package io.github.koronerap.pulse.data

import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException

/** Bir oyuncu sayısı örneği: Unix saniyesi ve sayı. */
@Serializable
data class PlayerSample(val t: Long, val c: Int)

@Serializable
data class PlayerHistory(
    val samples: MutableList<PlayerSample> = mutableListOf(),
    /** Son sorgu (Unix saniyesi); verisi olmayan app'te de dolu, her turda sorulmasın. */
    var lastCheckedAt: Long? = null,
    /** Son sorguda Steam'in verisi var mıydı (yayınlanmamış app'lerde yok). */
    var available: Boolean = false,
)

/**
 * Bir günün wishlist satırı; masaüstüyle aynı kısa adlar: eklenen, silinen, satın alınan,
 * hediye edilen ve satırın indirildiği gün ([f]). Gün bitmeden okunan satır yeniden iniyor.
 */
@Serializable
data class WishlistRow(val a: Int, val d: Int, val p: Int, val g: Int, val f: String? = null) {
    val net: Int get() = a - d - p - g
}

/** Bir app'in günlük wishlist kayıtları (GMT günleri, yyyy-MM-dd). */
@Serializable
data class WishlistLedger(
    var appMinDate: String? = null,
    val days: MutableMap<String, WishlistRow> = mutableMapOf(),
    var lastCheckedAt: Long? = null,
)

/**
 * Bir hesabın (anahtarın) satışları, günlere (Pasifik) ve app'lere göre. Telefonda yalnızca
 * son [AnalyticsService.SALES_WINDOW_DAYS] gün tutuluyor: widget bugünü, dünü, son 7 ve 30
 * günü gösteriyor; tüm geçmişi indirmek gereksiz istek olurdu.
 */
@Serializable
data class SalesLedger(
    var highwatermark: String = "0",
    val pendingDates: MutableList<String> = mutableListOf(),
    val days: MutableMap<String, MutableMap<Long, SalesTotals>> = mutableMapOf(),
    val appNames: MutableMap<Long, String> = mutableMapOf(),
    var lastCheckedAt: Long? = null,
    /** Sınır yayıncının paylaştıklarıyla (view grant) birlikte mi; 1.1.0 sormuyordu. */
    var includesViewGrants: Boolean = false,
)

@Serializable
data class AccountState(val failure: AnalyticsFailure? = null, val failureAt: Long? = null)

/** Son yenilemenin durumu; uygulama ve widget hatayı buradan gösteriyor. */
@Serializable
data class AnalyticsState(
    val accounts: MutableMap<String, AccountState> = mutableMapOf(),
    var backfillPending: Boolean = false,
    var lastRefreshAt: Long? = null,
)

/** JSON dosyası; yazma geçici dosyaya, sonra yerine taşınarak (yarıda kalırsa eskisi bozulmuyor). */
internal class JsonFile<T>(private val file: File, private val serializer: KSerializer<T>, private val create: () -> T) {
    fun load(): T {
        if (!file.exists()) return create()

        return try {
            STORE_JSON.decodeFromString(serializer, file.readText())
        } catch (_: SerializationException) {
            // Bozuk önbellek uygulamayı durdurmamalı: veri Steam'den yeniden alınabiliyor.
            create()
        } catch (_: IllegalArgumentException) {
            create()
        } catch (_: IOException) {
            create()
        }
    }

    fun save(value: T) {
        file.parentFile?.mkdirs()
        val temp = File(file.parentFile, "${file.name}.${System.nanoTime()}.tmp")

        try {
            temp.writeText(STORE_JSON.encodeToString(serializer, value))

            if (!temp.renameTo(file)) {
                file.delete()
                temp.renameTo(file)
            }
        } finally {
            temp.delete()
        }
    }

    companion object {
        val STORE_JSON = Json {
            ignoreUnknownKeys = true
            encodeDefaults = true
        }
    }
}

/** Analitik önbelleği (uygulamanın kendi klasöründe `analytics/`). */
class AnalyticsStore(val directory: File) {
    private fun players(appId: Long) = JsonFile(File(directory, "players/$appId.json"), PlayerHistory.serializer(), ::PlayerHistory)

    private fun wishlist(appId: Long) = JsonFile(File(directory, "wishlist/$appId.json"), WishlistLedger.serializer(), ::WishlistLedger)

    private fun sales(accountId: String) = JsonFile(File(directory, "sales/$accountId.json"), SalesLedger.serializer(), ::SalesLedger)

    private val state = JsonFile(File(directory, "state.json"), AnalyticsState.serializer(), ::AnalyticsState)

    fun loadPlayers(appId: Long) = players(appId).load()

    fun savePlayers(appId: Long, history: PlayerHistory) = players(appId).save(history)

    fun loadWishlist(appId: Long) = wishlist(appId).load()

    fun saveWishlist(appId: Long, ledger: WishlistLedger) = wishlist(appId).save(ledger)

    fun loadSales(accountId: String) = sales(accountId).load()

    fun saveSales(accountId: String, ledger: SalesLedger) = sales(accountId).save(ledger)

    fun loadState() = state.load()

    fun saveState(value: AnalyticsState) = state.save(value)

    /** Ayarlardaki "verileri sil": önbellek baştan iniyor; anahtarlar ve oyunlar kalıyor. */
    fun clear() {
        directory.deleteRecursively()
    }
}
