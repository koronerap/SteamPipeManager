package io.github.koronerap.pulse.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException

/** HTTP yanıtı: durum kodu ve gövde. */
data class HttpResponse(val status: Int, val body: String)

/** Tek yöntemli istemci; testlerde sahte sunucuyla değiştiriliyor. */
fun interface HttpGet {
    suspend fun get(url: String): HttpResponse
}

class UrlConnectionClient(private val timeoutMs: Int = 30_000) : HttpGet {
    override suspend fun get(url: String): HttpResponse = withContext(Dispatchers.IO) {
        val connection = URL(url).openConnection() as HttpURLConnection

        try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.setRequestProperty("Accept", "application/json")

            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            HttpResponse(status, stream?.bufferedReader()?.use { it.readText() } ?: "")
        } finally {
            connection.disconnect()
        }
    }
}

/** Analitik verisinin neden alınamadığı; arayüz ve widget kullanıcıya bunu söylüyor. */
enum class AnalyticsFailure { Network, InvalidKey, RateLimited, BadResponse }

/**
 * Steam Web API hatası. Mesaj istek adresini **içermiyor**: finans anahtarı adreste gidiyor
 * ve hata metinleri ekranda, log'larda görünebiliyor.
 */
class AnalyticsException(val failure: AnalyticsFailure, message: String, cause: Throwable? = null) : Exception(message, cause)

/** Steam Web API çağrıları için ortak yardımcılar; masaüstündeki SteamWebApi'nin karşılığı. */
internal object SteamWebApi {
    val json = Json { ignoreUnknownKeys = true }

    suspend fun getJson(http: HttpGet, url: String, notFoundIsEmpty: Boolean = false): JsonElement? {
        val response = try {
            http.get(url)
        } catch (e: IOException) {
            throw AnalyticsException(AnalyticsFailure.Network, "Steam could not be reached.", e)
        }

        return when {
            notFoundIsEmpty && response.status == 404 -> null
            response.status == 401 || response.status == 403 ->
                throw AnalyticsException(AnalyticsFailure.InvalidKey, "Steam rejected the key.")
            response.status == 429 ->
                throw AnalyticsException(AnalyticsFailure.RateLimited, "Steam is limiting requests; trying again later.")
            response.status !in 200..299 ->
                throw AnalyticsException(AnalyticsFailure.Network, "Steam answered with HTTP ${response.status}.")
            else -> try {
                json.parseToJsonElement(response.body)
            } catch (e: SerializationException) {
                throw AnalyticsException(AnalyticsFailure.BadResponse, "Steam's answer could not be read.", e)
            } catch (e: IllegalArgumentException) {
                throw AnalyticsException(AnalyticsFailure.BadResponse, "Steam's answer could not be read.", e)
            }
        }
    }

    /** Yanıtlar `{"response": {...}}` içinde geliyor; yoksa kökün kendisi. */
    fun body(root: JsonElement): JsonElement = (root as? JsonObject)?.get("response") ?: root

    private fun primitive(element: JsonElement?, name: String): JsonPrimitive? =
        ((element as? JsonObject)?.get(name) as? JsonPrimitive)?.takeUnless { it is JsonNull }

    // Valve'ın örneklerinde aynı türden sayılar kimi yerde tırnaklı, kimi yerde değil;
    // ikisi de okunuyor.

    fun long(element: JsonElement?, name: String): Long = primitive(element, name)?.content?.toLongOrNull() ?: 0

    /** Para alanları dört ondalıklı metin olarak geliyor: "311.7201". */
    fun money(element: JsonElement?, name: String): Double = primitive(element, name)?.content?.toDoubleOrNull() ?: 0.0

    fun text(element: JsonElement?, name: String): String? =
        primitive(element, name)?.takeIf { it.isString }?.content

    private val dateFormats = listOf("yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd").map(DateTimeFormatter::ofPattern)

    fun date(text: String?): LocalDate? {
        if (text.isNullOrEmpty()) return null

        for (format in dateFormats) {
            try {
                return LocalDate.parse(text, format)
            } catch (_: DateTimeParseException) {
            }
        }

        return null
    }

    fun dateText(date: LocalDate): String = date.toString()
}
