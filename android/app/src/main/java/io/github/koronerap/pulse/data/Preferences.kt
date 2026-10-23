package io.github.koronerap.pulse.data

import android.content.SharedPreferences
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.time.Duration
import java.time.Instant

/** Kullanıcının tercihleri; masaüstündeki analytics-settings.json'un karşılığı. */
class Preferences(private val prefs: SharedPreferences) {
    /** Null: telefonun bölgesinin para birimi; "USD": yalnızca dolar. */
    var currency: String?
        get() = prefs.getString("currency", null)
        set(value) = prefs.edit().putString("currency", value).apply()

    val effectiveCurrency: String get() = currency ?: PulseFormat.homeCurrency()

    var hideAmounts: Boolean
        get() = prefs.getBoolean("hideAmounts", false)
        set(value) = prefs.edit().putBoolean("hideAmounts", value).apply()

    /** Arka plan yenilemesinin aralığı; Android 15 dakikadan sık izin vermiyor. */
    var refreshMinutes: Int
        get() = prefs.getInt("refreshMinutes", 30)
        set(value) = prefs.edit().putInt("refreshMinutes", value.coerceAtLeast(15)).apply()

    var widgetLook: WidgetLook
        get() = read("widgetLook", WidgetLook.serializer()) ?: WidgetLook()
        set(value) = write("widgetLook", WidgetLook.serializer(), value)

    var appLook: AppLook
        get() = read("appLook", AppLook.serializer()) ?: AppLook()
        set(value) = write("appLook", AppLook.serializer(), value)

    private fun <T> read(name: String, serializer: KSerializer<T>): T? = prefs.getString(name, null)?.let { decode(serializer, it) }

    private fun <T> write(name: String, serializer: KSerializer<T>, value: T) =
        prefs.edit().putString(name, json.encodeToString(serializer, value)).apply()

    internal companion object {
        private val json = Json { ignoreUnknownKeys = true }

        /**
         * Görünüm JSON olarak saklanıyor: yeni bir seçenek eklenince eski kayıt varsayılanıyla
         * açılıyor; bozuk ya da bilinmeyen bir değer null (görünüm varsayılana dönüyor).
         */
        fun <T> decode(serializer: KSerializer<T>, text: String): T? = try {
            json.decodeFromString(serializer, text)
        } catch (_: SerializationException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        }

        fun <T> encode(serializer: KSerializer<T>, value: T): String = json.encodeToString(serializer, value)
    }
}

/**
 * Doların kullanıcının para birimindeki karşılığı, en son kur (frankfurter.dev). İstekte
 * yalnızca para birimi kodu gidiyor; altı saatte bir yenileniyor, ulaşılamazsa on beş
 * dakika beklenip eldekiyle devam ediliyor.
 */
class ExchangeRate(private val prefs: SharedPreferences, private val http: HttpGet, private val now: () -> Instant = Instant::now) {
    private var quietUntil: Instant = Instant.EPOCH

    /** Önbellekteki kur; para birimi değiştiyse ya da yoksa null. */
    fun cached(currency: String): Double? =
        if (prefs.getString("rateCurrency", null) == currency) prefs.getFloat("rate", 0f).toDouble().takeIf { it > 0 } else null

    suspend fun refresh(currency: String): Double? {
        if (currency == "USD") return null

        val fetchedAt = Instant.ofEpochSecond(prefs.getLong("rateFetchedAt", 0))
        val at = now()

        if (cached(currency) != null && Duration.between(fetchedAt, at) < Duration.ofHours(6)) return cached(currency)
        if (at < quietUntil) return cached(currency)

        return try {
            val body = SteamWebApi.getJson(http, "https://api.frankfurter.dev/v2/rate/USD/$currency") as? JsonObject
            val rate = body?.get("rate")?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content?.toDoubleOrNull() }

            if (rate != null && rate > 0) {
                prefs.edit()
                    .putString("rateCurrency", currency)
                    .putFloat("rate", rate.toFloat())
                    .putLong("rateFetchedAt", at.epochSecond)
                    .apply()
            }

            cached(currency)
        } catch (_: AnalyticsException) {
            quietUntil = at.plus(Duration.ofMinutes(15))
            cached(currency)
        }
    }
}
