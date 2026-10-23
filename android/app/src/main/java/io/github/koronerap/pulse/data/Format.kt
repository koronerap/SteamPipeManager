package io.github.koronerap.pulse.data

import java.math.RoundingMode
import java.text.DecimalFormat
import java.text.DecimalFormatSymbols
import java.text.NumberFormat
import java.util.Currency
import java.util.Locale

/** Analitik sayılarının biçimi; masaüstündeki AnalyticsFormat'ın karşılığı. */
object PulseFormat {
    /** Ekran paylaşırken tutarlar gizli ("$•••"); tercih her yenilemede buraya yazılıyor. */
    @Volatile
    var hideMoney: Boolean = false

    private const val HIDDEN = "•••"

    fun count(value: Int?): String = value?.let { NumberFormat.getIntegerInstance().format(it) } ?: "—"

    /** Değişim: "+12", "−3", "0". */
    fun signed(value: Int): String = when {
        value > 0 -> "+" + count(value)
        value < 0 -> "−" + count(-value)
        else -> "0"
    }

    // .NET gibi yarımlar yukarı (1.25 → 1.3); Java'nın varsayılanı yarımları çifte yuvarlıyor.
    private fun decimal(pattern: String) = DecimalFormat(pattern, DecimalFormatSymbols.getInstance()).apply { roundingMode = RoundingMode.HALF_UP }

    private fun number(digits: Int) = NumberFormat.getNumberInstance().apply {
        minimumFractionDigits = digits
        maximumFractionDigits = digits
        roundingMode = RoundingMode.HALF_UP
    }

    private fun oneDecimal(value: Double): String = decimal("0.#").format(value)

    /** Kısa sayı (widget için): 950, 1.2K, 46K, 1.3M. */
    fun compact(value: Int): String = when {
        value >= 1_000_000 -> oneDecimal(value / 1_000_000.0) + "M"
        value >= 10_000 -> decimal("0").format(value / 1_000.0) + "K"
        value >= 1_000 -> oneDecimal(value / 1_000.0) + "K"
        else -> value.toString()
    }

    fun compact(value: Int?): String = value?.let(::compact) ?: "—"

    fun usd(value: Double): String {
        if (hideMoney) return "$$HIDDEN"
        val text = number(2).format(kotlin.math.abs(value))
        return (if (value < 0) "−$" else "$") + text
    }

    /** Widget için kısa tutar: $227, $1.2K, −$40. */
    fun compactUsd(value: Double): String = compactAmount(value, "$")

    /** Kullanıcının para biriminde kısa tutar: ₺7.8K; simgesi olmayanlarda kodla (SEK 120). */
    fun compactMoney(value: Double, currency: String): String =
        if (currency.equals("USD", ignoreCase = true)) compactUsd(value) else compactAmount(value, symbolOf(currency))

    private fun compactAmount(value: Double, symbol: String): String {
        if (hideMoney) return "$symbol$HIDDEN"
        val rounded = kotlin.math.round(kotlin.math.abs(value)).toInt()
        return (if (value < 0) "−" else "") + symbol + if (rounded < 1000) rounded.toString() else compact(rounded)
    }

    /** Kullanıcının para birimindeki yaklaşık karşılık: "≈ ₺11,268"; yoksa boş. */
    fun local(amount: Double?, currency: String?): String {
        if (amount == null || currency == null) return ""
        val symbol = symbolOf(currency)
        if (hideMoney) return "≈ $symbol$HIDDEN"

        val abs = kotlin.math.abs(amount)
        return "≈ " + (if (amount < 0) "−" else "") + symbol + number(if (abs >= 100) 0 else 2).format(abs)
    }

    private val symbols = mapOf(
        "USD" to "$", "EUR" to "€", "GBP" to "£", "TRY" to "₺", "JPY" to "¥", "KRW" to "₩",
        "CNY" to "CN¥", "RUB" to "₽", "BRL" to "R$", "CAD" to "CA$", "AUD" to "A$", "PLN" to "zł ",
        "INR" to "₹", "CHF" to "CHF ", "MXN" to "MX$", "UAH" to "₴", "KZT" to "₸",
    )

    /** Ayarlarda sunulan para birimleri; masaüstüyle aynı liste. */
    val offeredCurrencies = listOf("USD", "EUR", "GBP", "TRY", "JPY", "KRW", "CNY", "RUB", "BRL", "CAD", "AUD", "PLN", "INR", "CHF", "MXN", "UAH", "KZT")

    fun symbolOf(code: String): String = symbols[code.uppercase()] ?: (code.uppercase() + " ")

    /** Telefonun bölgesinin para birimi (tr-TR → TRY); bilinmiyorsa dolar. */
    fun homeCurrency(locale: Locale = Locale.getDefault()): String = try {
        Currency.getInstance(locale).currencyCode
    } catch (_: IllegalArgumentException) {
        "USD"
    }
}
