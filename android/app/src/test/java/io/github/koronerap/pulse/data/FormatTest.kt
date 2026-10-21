package io.github.koronerap.pulse.data

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import java.util.Locale

/** Masaüstündeki biçim testlerinin karşılığı; aynı çıktılar. */
class FormatTest {
    private val original = Locale.getDefault()

    @Before
    fun english() = Locale.setDefault(Locale.US)

    @After
    fun restore() {
        Locale.setDefault(original)
        PulseFormat.hideMoney = false
    }

    @Test
    fun compactNumbersFitTheWidget() {
        assertEquals("950", PulseFormat.compact(950))
        assertEquals("1.2K", PulseFormat.compact(1234))
        assertEquals("46K", PulseFormat.compact(45678))
        assertEquals("1.3M", PulseFormat.compact(1_250_000))
        assertEquals("—", PulseFormat.compact(null as Int?))
    }

    @Test
    fun changesCarryTheirSignAndNegativeAmountsARealMinus() {
        assertEquals("+1,200", PulseFormat.signed(1200))
        assertEquals("−3", PulseFormat.signed(-3))
        assertEquals("−$5.00", PulseFormat.usd(-5.0))
        assertEquals("−$1.5K", PulseFormat.compactUsd(-1500.0))
        assertEquals("$227", PulseFormat.compactUsd(227.4))
        assertEquals("≈ ₺1,235", PulseFormat.local(1234.6, "TRY"))
        assertEquals("≈ SEK 12.50", PulseFormat.local(12.5, "SEK"))
    }

    @Test
    fun amountsCanBeHiddenButCountsStay() {
        PulseFormat.hideMoney = true

        assertEquals("$•••", PulseFormat.usd(5.0))
        assertEquals("$•••", PulseFormat.compactUsd(1500.0))
        assertEquals("≈ ₺•••", PulseFormat.local(100.0, "TRY"))
        assertEquals("1,500", PulseFormat.count(1500))
    }

    @Test
    fun theHomeCurrencyComesFromTheRegion() {
        assertEquals("TRY", PulseFormat.homeCurrency(Locale("tr", "TR")))
        assertEquals("JPY", PulseFormat.homeCurrency(Locale.JAPAN))
        assertEquals("USD", PulseFormat.homeCurrency(Locale("en")))
    }
}
