package io.github.koronerap.pulse.data

import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.LocalDate

/**
 * Masaüstündeki AnalyticsTests'in telefona taşınan kısmı: aynı senaryolar, aynı sonuçlar.
 * Ağa çıkılmıyor; saat elle ilerletiliyor.
 */
class AnalyticsServiceTest {
    private val key = "FINANCIAL-KEY-123"
    private val game = 4000L
    private val dir: File = Files.createTempDirectory("pulse-test").toFile()
    private var now: Instant = Instant.parse("2026-10-20T12:00:00Z")
    private val steam = FakeSteam(key)

    @After
    fun cleanUp() {
        dir.deleteRecursively()
    }

    private fun service(budget: Int = 1000) = AnalyticsService(
        AnalyticsStore(dir),
        SteamPlayerCountClient(steam),
        SteamFinancialClient(steam),
        now = { now },
    ).apply {
        financialRequestBudget = budget
        financialRequestSpacing = Duration.ZERO
    }

    private fun scope(withKey: Boolean = true) =
        AnalyticsScope(listOf(game), if (withKey) listOf(FinancialAccount(key, listOf(game))) else emptyList())

    private val account get() = FinancialAccount.fingerprint(key)
    private val utcToday get() = AnalyticsService.utcDay(now)
    private val pacificToday get() = AnalyticsService.pacificDay(now)

    @Test
    fun playersAreSampledOncePerIntervalAndThePeakComesFromTheSamples() = runTest {
        val service = service()

        for (count in listOf(100, 250, 180)) {
            steam.players[game] = count
            service.refresh(scope(withKey = false))
            service.refresh(scope(withKey = false))
            now = now.plus(Duration.ofMinutes(5))
        }

        val players = service.get(game, null).players
        assertEquals(3, steam.calls("GetNumberOfCurrentPlayers"))
        assertEquals(180, players.current)
        assertEquals(250, players.peak24Hours)
        assertEquals(3, players.last24Hours.size)
    }

    @Test
    fun anUnreleasedAppHasNoPlayersButIsNotAnError() = runTest {
        service().refresh(scope(withKey = false))

        val players = service().get(game, null).players
        assertNull(players.current)
        assertFalse(players.available)
    }

    private fun tenDaysOfWishlists() {
        steam.appMinDate[game] = utcToday.minusDays(9)

        for (i in 0..9) {
            steam.wishlists[game to utcToday.minusDays(i.toLong())] = listOf(10, 2, 1, 1)
        }
    }

    @Test
    fun theWishlistTotalAddsUpEveryDaySinceTheFirst() = runTest {
        tenDaysOfWishlists()
        service().refresh(scope())

        val wishlist = service().get(game, null).wishlist!!
        assertEquals(60, wishlist.total)
        assertEquals(6, wishlist.today.net)
        assertTrue(wishlist.isComplete)
    }

    @Test
    fun theWishlistHistoryDownloadsInStepsWithinTheBudget() = runTest {
        tenDaysOfWishlists()
        val results = mutableListOf<RefreshResult>()

        // Bütçe 4 istek: bugün, dün, değişen satış günleri ve bir geçmiş günü.
        repeat(6) { results.add(service(budget = 4).refresh(scope())) }

        assertEquals(RefreshResult.MorePending, results.first())
        assertEquals(RefreshResult.Done, results.last())
        assertEquals(60, service().get(game, null).wishlist!!.total)
    }

    private fun someSales() {
        val today = pacificToday
        val yesterday = today.minusDays(1)

        steam.changes.add(100L to yesterday)
        steam.changes.add(101L to today)

        // Paket satırı ana oyuna, oyun içi satış kendi appid'sine; yalnızca etkinleştirme
        // olan satır satış sayılmıyor. Sayfa boyu 2: üç sayfa.
        steam.sales[yesterday] = listOf(
            """{"line_item_type":"Package","packageid":1,"primary_appid":4000,"gross_units_sold":10,"gross_units_returned":-2,"net_units_sold":8,"gross_sales_usd":"100.0000","gross_returns_usd":"-20.0000","net_tax_usd":"7.5000","net_sales_usd":"72.5000","additional_revenue_share_tier":1}""",
            """{"line_item_type":"MicroTxn","appid":4001,"primary_appid":0,"gross_units_sold":3,"net_units_sold":3,"gross_sales_usd":"6.0000","net_sales_usd":"5.0000"}""",
            """{"line_item_type":"Package","packageid":2,"primary_appid":4000,"gross_units_activated":50,"gross_sales_usd":"0.0000","net_sales_usd":"0.0000"}""",
        )
        steam.sales[today] = listOf(
            """{"line_item_type":"Package","packageid":1,"primary_appid":"4000","gross_units_sold":"2","net_units_sold":"2","gross_sales_usd":"20.0000","net_sales_usd":"14.2500"}""",
        )
    }

    @Test
    fun salesAreTotalledPerAppAcrossPagesWithRefundsTaxAndShare() = runTest {
        someSales()
        service().refresh(scope())

        val sales = service().get(game, account).sales!!
        assertEquals(2, sales.today.units)
        assertEquals(14.25, sales.today.net, 1e-9)
        assertEquals(14.25 * 0.70, sales.today.share, 1e-9)

        assertEquals(8, sales.yesterday.units)
        assertEquals(72.5, sales.yesterday.net, 1e-9)
        assertEquals(20.0, sales.yesterday.returns, 1e-9)
        assertEquals(7.5, sales.yesterday.tax, 1e-9)
        assertEquals(72.5 * 0.75, sales.yesterday.share, 1e-9)

        assertEquals(86.75, sales.last7Days.net, 1e-9)
        assertTrue(sales.isComplete)
    }

    /** Telefonda yalnızca son günler: ilk sorgu bütün geçmişi bildirse de eskileri indirilmiyor. */
    @Test
    fun onlyRecentSalesDaysAreDownloaded() = runTest {
        someSales()
        val old = pacificToday.minusDays(200)
        steam.changes.add(99L to old)
        steam.sales[old] = listOf("""{"line_item_type":"Package","primary_appid":4000,"net_units_sold":1,"gross_sales_usd":"1.0","net_sales_usd":"1.0"}""")

        service().refresh(scope())

        assertEquals(setOf(pacificToday, pacificToday.minusDays(1)), steam.salesDatesRequested)
        assertFalse(AnalyticsStore(dir).loadSales(account).days.containsKey(old.toString()))
    }

    @Test
    fun aRejectedKeyIsRecordedAndOtherDataStays() = runTest {
        steam.players[game] = 42
        service().refresh(AnalyticsScope(listOf(game), listOf(FinancialAccount("WRONG", listOf(game)))))

        val state = service().state()
        assertEquals(AnalyticsFailure.InvalidKey, state.accounts.values.single().failure)
        assertEquals(42, service().get(game, null).players.current)
    }

    @Test
    fun errorMessagesNeverContainTheKey() = runTest {
        val client = SteamFinancialClient(FakeSteam("other"))

        val error = runCatching { client.changedDates(key, "0") }.exceptionOrNull() as AnalyticsException
        assertEquals(AnalyticsFailure.InvalidKey, error.failure)
        assertFalse(error.message!!.contains(key))
    }

    @Test
    fun missingWishlistDaysAreNewestFirstAndSkipFinalRows() {
        val ledger = WishlistLedger(
            appMinDate = "2026-10-10",
            days = mutableMapOf(
                "2026-10-17" to WishlistRow(1, 0, 0, 0, "2026-10-19"),
                "2026-10-16" to WishlistRow(1, 0, 0, 0, "2026-10-17"),
            ),
        )

        val missing = AnalyticsService.missingWishlistDays(ledger, LocalDate.parse("2026-10-14"), LocalDate.parse("2026-10-20")).toList()
        assertEquals(listOf("2026-10-18", "2026-10-16", "2026-10-15", "2026-10-14").map(LocalDate::parse), missing)
    }
}
