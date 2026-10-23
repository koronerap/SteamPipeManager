package io.github.koronerap.pulse.data

import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset

/** Bir finans anahtarı ve onunla sorgulanan app'ler (o şirketin oyunları). */
data class FinancialAccount(val key: String, val appIds: List<Long>) {
    /** Anahtarın kimliği; dosya adlarında ve durumda anahtarın kendisi yerine. */
    val id: String = fingerprint(key)

    companion object {
        /** Masaüstüyle aynı: SHA-256'nın ilk 16 onaltılık hanesi. */
        fun fingerprint(key: String): String =
            MessageDigest.getInstance("SHA-256").digest(key.trim().toByteArray()).joinToString("") { "%02x".format(it) }.take(16)
    }
}

/** Neyin yenileneceği: oyuncu sayısı bütün app'ler için, finans verisi her app için sahibinin anahtarıyla. */
data class AnalyticsScope(val appIds: List<Long>, val accounts: List<FinancialAccount>)

enum class RefreshResult { Done, MorePending, Busy }

data class PlayerStats(val current: Int?, val at: Instant?, val peak24Hours: Int?, val last24Hours: List<PlayerSample>, val available: Boolean)

data class WishlistCounts(val adds: Int = 0, val deletes: Int = 0, val purchases: Int = 0, val gifts: Int = 0) {
    val net: Int get() = adds - deletes - purchases - gifts
}

/** [total]: wishlist'te duran; Steam vermiyor, günlük hareketlerden toplanıyor. [isComplete]: bütün günler indi mi. */
data class WishlistStats(val total: Int, val today: WishlistCounts, val yesterday: WishlistCounts, val isComplete: Boolean)

/** Satışlar Pasifik günlerine göre. */
data class SalesStats(val today: SalesTotals, val yesterday: SalesTotals, val last7Days: SalesTotals, val last30Days: SalesTotals, val isComplete: Boolean)

data class AppAnalytics(val appId: Long, val players: PlayerStats, val wishlist: WishlistStats?, val sales: SalesStats?)

/**
 * Analitik verisini yeniler ve önbellekten özetler; masaüstündeki AnalyticsService'in
 * telefona uyarlanmışı. Aynı algoritma: oyuncu sayısı her turda (aralık dolduysa), wishlist
 * ve satışlar 3 saatte bir; wishlist geçmişi ilk günden bugüne, istek bütçesiyle yavaşça.
 * Farkı: satışların yalnızca son [SALES_WINDOW_DAYS] günü indiriliyor.
 */
class AnalyticsService(
    private val store: AnalyticsStore,
    private val players: SteamPlayerCountClient,
    private val financial: SteamFinancialClient,
    private val now: () -> Instant = Instant::now,
) {
    var playersInterval: Duration = Duration.ofMinutes(5)
    var financialInterval: Duration = Duration.ofHours(3)

    /** Bir yenilemede finans servisine en fazla bu kadar istek. */
    var financialRequestBudget = 100

    /** Finans istekleri arasındaki en kısa süre; Valve sınır yayınlamıyor, temkinli. */
    var financialRequestSpacing: Duration = Duration.ofMillis(300)

    /** Widget ve uygulama aynı anda yenilemesin. */
    private val mutex = Mutex()

    suspend fun refresh(scope: AnalyticsScope, force: Boolean = false): RefreshResult {
        if (!mutex.tryLock()) return RefreshResult.Busy

        try {
            val at = now()

            for (appId in scope.appIds.distinct()) {
                refreshPlayers(appId, at, force)
            }

            val state = store.loadState()
            state.lastRefreshAt = at.epochSecond

            // Artık kullanılmayan anahtarların durumu kalmasın.
            state.accounts.keys.retainAll(scope.accounts.map { it.id }.toSet())

            val budget = RequestBudget(financialRequestBudget, financialRequestSpacing)
            financial.beforeRequest = budget::take
            var pending = false

            try {
                // Önce her hesabın güncel verisi, sonra geçmiş: ilk açılışta da sayılar hemen dolsun.
                val reachable = scope.accounts.filter { account ->
                    tryRun(account, state, at) { refreshRecent(account, at, force) }
                }

                for (account in reachable) {
                    tryRun(account, state, at) { backfill(account, at) }
                }
            } catch (_: BudgetExhausted) {
                pending = true
            } finally {
                financial.beforeRequest = null
            }

            state.backfillPending = pending
            store.saveState(state)
            return if (pending) RefreshResult.MorePending else RefreshResult.Done
        } finally {
            mutex.unlock()
        }
    }

    /** Bir hesabın işi; Steam'in hatası yalnızca o hesabın durumuna yazılıyor. Bütçe bitişi yukarı çıkıyor. */
    private suspend fun tryRun(account: FinancialAccount, state: AnalyticsState, at: Instant, work: suspend () -> Unit): Boolean =
        try {
            work()
            state.accounts[account.id] = AccountState()
            true
        } catch (e: AnalyticsException) {
            state.accounts[account.id] = AccountState(e.failure, at.epochSecond)
            false
        }

    private suspend fun refreshPlayers(appId: Long, at: Instant, force: Boolean) {
        val history = store.loadPlayers(appId)

        if (!force && history.lastCheckedAt?.let { Duration.between(Instant.ofEpochSecond(it), at) < playersInterval } == true) {
            return
        }

        val count = try {
            players.currentPlayers(appId)
        } catch (_: AnalyticsException) {
            // Ağ yok: sorgu zamanı güncellenmiyor, bir sonraki turda yeniden deneniyor.
            return
        }

        history.lastCheckedAt = at.epochSecond
        history.available = count != null
        count?.let { history.samples.add(PlayerSample(at.epochSecond, it)) }

        val oldest = at.minus(PLAYER_RETENTION).epochSecond
        history.samples.removeAll { it.t < oldest }
        store.savePlayers(appId, history)
    }

    private suspend fun refreshRecent(account: FinancialAccount, at: Instant, force: Boolean) {
        for (appId in account.appIds.distinct()) {
            refreshRecentWishlist(account.key, appId, at, force)
        }

        refreshChangedSales(account, at, force)
    }

    private suspend fun backfill(account: FinancialAccount, at: Instant) {
        downloadPendingSales(account)

        for (appId in account.appIds.distinct()) {
            backfillWishlist(account.key, appId, at)
        }
    }

    private suspend fun refreshRecentWishlist(key: String, appId: Long, at: Instant, force: Boolean) {
        val ledger = store.loadWishlist(appId)

        if (!force && ledger.lastCheckedAt?.let { Duration.between(Instant.ofEpochSecond(it), at) < financialInterval } == true) {
            return
        }

        val today = utcDay(at)

        try {
            for (date in listOf(today, today.minusDays(1))) {
                record(ledger, financial.wishlistDay(key, appId, date), today)
            }

            ledger.lastCheckedAt = at.epochSecond
        } finally {
            store.saveWishlist(appId, ledger)
        }
    }

    /** Eksik ya da gün bitmeden okunmuş günleri en yeniden geriye doğru indirir. */
    private suspend fun backfillWishlist(key: String, appId: Long, at: Instant) {
        val ledger = store.loadWishlist(appId)
        val first = SteamWebApi.date(ledger.appMinDate) ?: return
        val today = utcDay(at)
        var written = 0

        try {
            for (date in missingWishlistDays(ledger, first, today)) {
                record(ledger, financial.wishlistDay(key, appId, date), today)

                if (++written % SAVE_EVERY == 0) {
                    store.saveWishlist(appId, ledger)
                }
            }
        } finally {
            store.saveWishlist(appId, ledger)
        }
    }

    private suspend fun refreshChangedSales(account: FinancialAccount, at: Instant, force: Boolean) {
        val ledger = store.loadSales(account.id)

        // Yayıncının paylaştığı satışlar (view grant) 1.1.0'da istenmiyordu; o dosyadaki sınır
        // onlarsız. Bir kez, günler baştan soruluyor.
        val restart = !ledger.includesViewGrants

        if (!force && !restart && ledger.lastCheckedAt?.let { Duration.between(Instant.ofEpochSecond(it), at) < financialInterval } == true) {
            return
        }

        val changed = financial.changedDates(account.key, if (restart) "0" else ledger.highwatermark)
        val since = pacificDay(at).minusDays(SALES_WINDOW_DAYS - 1)

        for (date in changed.dates.filter { it >= since }.map(SteamWebApi::dateText)) {
            if (date !in ledger.pendingDates) {
                ledger.pendingDates.add(date)
            }
        }

        // Pencereden çıkan günler tutulmuyor.
        ledger.days.keys.removeAll { (SteamWebApi.date(it) ?: since) < since }
        ledger.highwatermark = changed.highwatermark
        ledger.includesViewGrants = true
        ledger.lastCheckedAt = at.epochSecond
        store.saveSales(account.id, ledger)
    }

    private suspend fun downloadPendingSales(account: FinancialAccount) {
        val ledger = store.loadSales(account.id)
        if (ledger.pendingDates.isEmpty()) return

        var written = 0

        try {
            // En yeni günler önce: bugün ve dün hemen görünsün.
            for (text in ledger.pendingDates.sortedDescending()) {
                val date = SteamWebApi.date(text)

                if (date == null) {
                    ledger.pendingDates.remove(text)
                    continue
                }

                val day = financial.salesDay(account.key, date)

                // Değişen gün baştan okunuyor ve yerine yazılıyor; üstüne eklenmiyor.
                ledger.days[text] = day.byApp.toMutableMap()
                ledger.appNames.putAll(day.appNames)
                ledger.pendingDates.remove(text)

                if (++written % SAVE_EVERY == 0) {
                    store.saveSales(account.id, ledger)
                }
            }
        } finally {
            store.saveSales(account.id, ledger)
        }
    }

    /** Bir app'in önbellekteki özeti; ağa çıkmıyor. Hesap yoksa satış gösterilmiyor. */
    fun get(appId: Long, accountId: String?): AppAnalytics {
        val at = now()
        val sales = accountId?.let(store::loadSales)

        return AppAnalytics(
            appId,
            summarizePlayers(store.loadPlayers(appId), at),
            summarizeWishlist(store.loadWishlist(appId), at),
            sales?.takeIf { it.lastCheckedAt != null }?.let { summarizeSales(it, at, appId) },
        )
    }

    fun state(): AnalyticsState = store.loadState()

    private fun summarizePlayers(history: PlayerHistory, at: Instant): PlayerStats {
        val since = at.minus(Duration.ofHours(24)).epochSecond
        val day = history.samples.filter { it.t >= since }.sortedBy { it.t }
        val latest = history.samples.maxByOrNull { it.t }

        return PlayerStats(
            if (history.available) latest?.c else null,
            latest?.let { Instant.ofEpochSecond(it.t) },
            day.maxOfOrNull { it.c },
            day,
            history.available,
        )
    }

    private fun summarizeWishlist(ledger: WishlistLedger, at: Instant): WishlistStats? {
        if (ledger.days.isEmpty()) return null

        val today = utcDay(at)

        fun on(date: LocalDate) = ledger.days[SteamWebApi.dateText(date)]?.let { WishlistCounts(it.a, it.d, it.p, it.g) } ?: WishlistCounts()

        val first = SteamWebApi.date(ledger.appMinDate)
        val complete = first != null && missingWishlistDays(ledger, first, today).none()

        return WishlistStats(ledger.days.values.sumOf { it.net }, on(today), on(today.minusDays(1)), complete)
    }

    private fun summarizeSales(ledger: SalesLedger, at: Instant, appId: Long): SalesStats {
        val today = pacificDay(at)

        fun on(date: LocalDate) = ledger.days[SteamWebApi.dateText(date)]?.get(appId) ?: SalesTotals.ZERO

        fun range(days: Long) = (0 until days).fold(SalesTotals.ZERO) { sum, i -> sum + on(today.minusDays(i)) }

        return SalesStats(on(today), on(today.minusDays(1)), range(7), range(30), ledger.pendingDates.isEmpty())
    }

    private class BudgetExhausted : Exception("Request budget used up for this refresh.")

    /** Bir yenilemedeki finans istekleri: sayı sınırı ve aralarında bekleme. */
    private class RequestBudget(private val limit: Int, private val spacing: Duration) {
        private var used = 0
        private var last: Long? = null

        suspend fun take() {
            if (used >= limit) throw BudgetExhausted()

            last?.let {
                val wait = spacing.toMillis() - (System.currentTimeMillis() - it)
                if (wait > 0) delay(wait)
            }

            used++
            last = System.currentTimeMillis()
        }
    }

    companion object {
        /** Telefonda oyuncu örnekleri bir hafta tutuluyor; widget yalnızca son 24 saati gösteriyor. */
        val PLAYER_RETENTION: Duration = Duration.ofDays(7)

        /** Satışların indirilen penceresi: son 30 gün ve birkaç gün pay. */
        const val SALES_WINDOW_DAYS = 35L

        /** Her 25 günde bir kaydediliyor; yarıda kalan ilerleme kaybolmasın. */
        private const val SAVE_EVERY = 25

        private val PACIFIC: ZoneId = ZoneId.of("America/Los_Angeles")

        fun utcDay(at: Instant): LocalDate = at.atOffset(ZoneOffset.UTC).toLocalDate()

        /** Satış günleri Pasifik saatiyle (Valve'ın verdiği ve beklediği tarih). */
        fun pacificDay(at: Instant): LocalDate = at.atZone(PACIFIC).toLocalDate()

        /** Yeniden istenmesi gereken günler, en yeniden: hiç inmemiş ya da bittikten sonra okunmamış. */
        internal fun missingWishlistDays(ledger: WishlistLedger, first: LocalDate, today: LocalDate): Sequence<LocalDate> =
            generateSequence(today.minusDays(2)) { it.minusDays(1) }
                .takeWhile { it >= first }
                .filter { date ->
                    val row = ledger.days[SteamWebApi.dateText(date)]
                    row == null || !isFinal(row, date)
                }

        /** Valve günü ertesi gün içinde tamamlıyor; iki gün sonra okunan satır kesin. */
        private fun isFinal(row: WishlistRow, date: LocalDate): Boolean =
            SteamWebApi.date(row.f)?.let { it >= date.plusDays(2) } == true

        private fun record(ledger: WishlistLedger, day: WishlistDay, today: LocalDate) {
            ledger.days[SteamWebApi.dateText(day.date)] = WishlistRow(day.adds, day.deletes, day.purchases, day.gifts, SteamWebApi.dateText(today))
            day.appMinDate?.let { ledger.appMinDate = SteamWebApi.dateText(it) }
        }
    }
}
