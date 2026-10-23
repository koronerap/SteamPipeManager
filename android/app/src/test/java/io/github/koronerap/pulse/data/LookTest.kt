package io.github.koronerap.pulse.data

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.Locale

/** Widget'ın görünümü: yerleşim eşikleri, oyunların sırası, satış dönemi, kayıt. */
class LookTest {
    private val original = Locale.getDefault()

    @Before
    fun english() = Locale.setDefault(Locale.US)

    @After
    fun restore() {
        Locale.setDefault(original)
        PulseFormat.hideMoney = false
    }

    /** Glance'in boyut basamakları: 110 ve 220 dp yükseklik. Başlık kapalıyken bir oyun daha. */
    @Test
    fun rowsFollowTheHeightTheTextSizeAndTheHeader() {
        assertEquals(3, WidgetLayout.rows(110f, 1f, header = true))
        assertEquals(4, WidgetLayout.rows(110f, 1f, header = false))
        assertEquals(7, WidgetLayout.rows(220f, 1f, header = true))
        assertEquals(5, WidgetLayout.rows(220f, LookTextSize.ExtraLarge.scale, header = true))
        assertEquals(1, WidgetLayout.rows(60f, 2f, header = true))
    }

    @Test
    fun numbersAreDroppedFromTheEndWhenTheWidgetIsNarrow() {
        assertEquals(1, WidgetLayout.singleMetrics(110f, 1f))
        assertEquals(2, WidgetLayout.singleMetrics(170f, 1f))
        assertEquals(3, WidgetLayout.singleMetrics(250f, 1f))
        assertEquals(1, WidgetLayout.singleMetrics(170f, LookTextSize.ExtraLarge.scale))

        // Ad yanında yer kaplıyor; gizliyken aynı genişliğe daha çok sayı.
        assertEquals(3, WidgetLayout.rowMetrics(250f, 1f, titles = true))
        assertEquals(1, WidgetLayout.rowMetrics(170f, 1f, titles = true))
        assertEquals(2, WidgetLayout.rowMetrics(170f, 1f, titles = false))

        assertTrue(WidgetLayout.wide(330f, 1f))
        assertFalse(WidgetLayout.wide(330f, LookTextSize.Large.scale))
        assertFalse(WidgetLayout.wide(250f, LookTextSize.Small.scale))
    }

    private fun game(id: Long, players: Int?, todayNet: Double?, weekNet: Double = 0.0) = AppAnalytics(
        id,
        PlayerStats(players, null, null, emptyList(), players != null),
        null,
        todayNet?.let { SalesStats(SalesTotals(net = it), SalesTotals(), SalesTotals(net = weekNet), SalesTotals(), true) },
    )

    @Test
    fun gamesCanBeOrderedByPlayersOrByTheChosenPeriodsSales() {
        val games = listOf(game(1, 10, 5.0, weekNet = 90.0), game(2, null, null), game(3, 500, 40.0, weekNet = 50.0))

        assertEquals(listOf(1L, 2L, 3L), WidgetLook().ordered(games) { it }.map { it.appId })
        assertEquals(listOf(3L, 1L, 2L), WidgetLook(order = GameOrder.Players).ordered(games) { it }.map { it.appId })
        assertEquals(listOf(3L, 1L, 2L), WidgetLook(order = GameOrder.Sales).ordered(games) { it }.map { it.appId })
        assertEquals(
            listOf(1L, 3L, 2L),
            WidgetLook(order = GameOrder.Sales, salesPeriod = SalesPeriod.Week).ordered(games) { it }.map { it.appId },
        )
    }

    @Test
    fun amountsCanBeShownInTheUsersCurrency() {
        assertEquals("₺7.8K", PulseFormat.compactMoney(7_800.0, "TRY"))
        assertEquals("−₺40", PulseFormat.compactMoney(-40.0, "TRY"))
        assertEquals("$1.2K", PulseFormat.compactMoney(1_200.0, "USD"))
        assertEquals("SEK 120", PulseFormat.compactMoney(120.0, "SEK"))

        PulseFormat.hideMoney = true
        assertEquals("₺•••", PulseFormat.compactMoney(7_800.0, "TRY"))
    }

    /** Eski sürümün kaydı yeni seçenekleri varsayılanıyla açıyor; bozuk kayıt hiç açılmıyor. */
    @Test
    fun aSavedLookSurvivesNewOptionsButNotABadValue() {
        val saved = Preferences.decode(WidgetLook.serializer(), """{"theme":"Light","textSize":"Large","futureOption":1}""")!!

        assertEquals(LookTheme.Light, saved.theme)
        assertEquals(LookTextSize.Large, saved.textSize)
        assertEquals(WidgetLook().transparency, saved.transparency)

        assertNull(Preferences.decode(WidgetLook.serializer(), """{"theme":"Purple"}"""))
        assertNull(Preferences.decode(WidgetLook.serializer(), "not json"))

        val look = WidgetLook(order = GameOrder.Sales, localCurrency = true)
        assertEquals(look, Preferences.decode(WidgetLook.serializer(), Preferences.encode(WidgetLook.serializer(), look)))
    }
}
