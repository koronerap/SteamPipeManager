package io.github.koronerap.pulse

import android.content.Context
import androidx.glance.appwidget.updateAll
import io.github.koronerap.pulse.data.AnalyticsFailure
import io.github.koronerap.pulse.data.AnalyticsScope
import io.github.koronerap.pulse.data.AnalyticsService
import io.github.koronerap.pulse.data.AnalyticsStore
import io.github.koronerap.pulse.data.AppAnalytics
import io.github.koronerap.pulse.data.ExchangeRate
import io.github.koronerap.pulse.data.FinancialAccount
import io.github.koronerap.pulse.data.FinancialKeys
import io.github.koronerap.pulse.data.GameRepository
import io.github.koronerap.pulse.data.KeystoreBox
import io.github.koronerap.pulse.data.Preferences
import io.github.koronerap.pulse.data.PulseFormat
import io.github.koronerap.pulse.data.RefreshResult
import io.github.koronerap.pulse.data.SteamFinancialClient
import io.github.koronerap.pulse.data.SteamPlayerCountClient
import io.github.koronerap.pulse.data.SteamStoreClient
import io.github.koronerap.pulse.data.TrackedGame
import io.github.koronerap.pulse.data.UrlConnectionClient
import io.github.koronerap.pulse.widget.PulseWidget
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.StateFlow
import java.io.File
import java.time.Instant

/** Bir oyunun ekranda gösterilen özeti. */
data class GameSummary(val game: TrackedGame, val analytics: AppAnalytics, val hasKey: Boolean)

/** Uygulamanın bütün durumunun anlık görüntüsü; ekran ve widget bundan çiziliyor. */
data class Snapshot(
    val games: List<GameSummary>,
    val failures: Map<String, AnalyticsFailure>,
    val backfillPending: Boolean,
    val lastRefreshAt: Instant?,
    val currency: String,
    val rate: Double?,
) {
    companion object {
        /** Açılışta, dosyalar arka planda okunana kadar. */
        val EMPTY = Snapshot(emptyList(), emptyMap(), false, null, "USD", null)
    }
}

/**
 * Parçaları bir arada kuran ve tutan nesne. Uygulama ve widget aynı süreçte; tek kopya
 * aynı anda iki yenilemeyi önlüyor (AnalyticsService içindeki kilit).
 */
class Pulse private constructor(context: Context) {
    private val app = context.applicationContext
    private val http = UrlConnectionClient()

    val store = AnalyticsStore(File(app.filesDir, "analytics"))
    val games = GameRepository(File(app.filesDir, "games.json"))
    val keys = FinancialKeys(app.getSharedPreferences("financial-keys", Context.MODE_PRIVATE), KeystoreBox())
    val preferences = Preferences(app.getSharedPreferences("settings", Context.MODE_PRIVATE))
    val storeClient = SteamStoreClient(http)
    private val rates = ExchangeRate(app.getSharedPreferences("rates", Context.MODE_PRIVATE), http)
    private val service = AnalyticsService(store, SteamPlayerCountClient(http), SteamFinancialClient(http))

    // Açılışta diske dokunulmuyor (ana iş parçacığı): ilk anlık görüntü changed() ile, arka planda.
    private val _snapshot = MutableStateFlow(Snapshot.EMPTY)

    /** Her yenilemeden ve her değişiklikten sonra güncelleniyor. */
    val snapshots: StateFlow<Snapshot> = _snapshot

    /** Oyunlardan ve anahtarlardan yenileme kapsamı: her anahtar bir hesap. */
    fun scope(): AnalyticsScope {
        val list = games.load()

        val accounts = list.filter { it.keyId != null }.groupBy { it.keyId!! }.mapNotNull { (keyId, owned) ->
            keys.read(keyId)?.let { FinancialAccount(it, owned.map(TrackedGame::appId)) }
        }

        return AnalyticsScope(list.map(TrackedGame::appId), accounts)
    }

    fun snapshot(): Snapshot {
        PulseFormat.hideMoney = preferences.hideAmounts
        val state = service.state()
        val currency = preferences.effectiveCurrency

        val summaries = games.load().map { game ->
            val hasKey = game.keyId != null && keys.read(game.keyId) != null
            GameSummary(game, service.get(game.appId, if (hasKey) game.keyId else null), hasKey)
        }

        return Snapshot(
            summaries,
            state.accounts.mapNotNull { (id, s) -> s.failure?.let { id to it } }.toMap(),
            state.backfillPending,
            state.lastRefreshAt?.let(Instant::ofEpochSecond),
            currency,
            rates.cached(currency),
        )
    }

    /** Değişiklikten sonra (oyun eklendi, ayar değişti): ekran ve widget yeniden çiziliyor. */
    suspend fun changed() {
        _snapshot.value = withContext(Dispatchers.IO) { snapshot() }
        PulseWidget().updateAll(app)
    }

    suspend fun refresh(force: Boolean): RefreshResult {
        val result = withContext(Dispatchers.IO) {
            service.refresh(scope(), force).also { rates.refresh(preferences.effectiveCurrency) }
        }

        changed()
        return result
    }

    companion object {
        @Volatile
        private var instance: Pulse? = null

        fun get(context: Context): Pulse =
            instance ?: synchronized(this) { instance ?: Pulse(context).also { instance = it } }
    }
}
