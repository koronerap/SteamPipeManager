package io.github.koronerap.pulse.data

import java.net.URLDecoder
import java.time.LocalDate

/**
 * Steam'in yerine sahte sunucu; masaüstü testlerindeki FakeSteamApi'nin karşılığı. Yanıt
 * biçimleri Valve'ın belgelerindeki örneklerden.
 */
class FakeSteam(private val validKey: String) : HttpGet {
    val players = mutableMapOf<Long, Int>()
    val appMinDate = mutableMapOf<Long, LocalDate>()
    val wishlists = mutableMapOf<Pair<Long, LocalDate>, List<Int>>()

    /** Değişiklik günlüğü: sınır değeri ve değişen gün. */
    val changes = mutableListOf<Pair<Long, LocalDate>>()
    val sales = mutableMapOf<LocalDate, List<String>>()

    /** Yayıncının paylaştığı (view grant) değişiklikler ve satırlar: yalnızca include_view_grants=true ile. */
    val grantedChanges = mutableListOf<Pair<Long, LocalDate>>()
    val grantedSales = mutableMapOf<LocalDate, List<String>>()

    /** Gönderilen sınırlar, sırayla. */
    val highwatermarksSent = mutableListOf<String>()
    var pageSize = 2

    /** İstenen adresler (yol; sorgu ve anahtar olmadan). */
    val requests = mutableListOf<String>()

    /** Satırları istenen satış günleri. */
    val salesDatesRequested = mutableSetOf<LocalDate>()

    fun calls(fragment: String) = requests.count { it.contains(fragment) }

    private fun query(url: String): Map<String, String> =
        url.substringAfter('?', "").split('&').filter { it.contains('=') }.associate {
            it.substringBefore('=') to URLDecoder.decode(it.substringAfter('='), "UTF-8")
        }

    override suspend fun get(url: String): HttpResponse {
        requests.add(url.substringBefore('?'))
        val q = query(url)

        if ("GetNumberOfCurrentPlayers" in url) {
            val count = players[q.getValue("appid").toLong()]
            return if (count != null) {
                HttpResponse(200, """{"response":{"player_count":$count,"result":1}}""")
            } else {
                HttpResponse(404, """{"response":{"result":42}}""")
            }
        }

        if (q["key"] != validKey) {
            return HttpResponse(403, "<html><body>Access is denied. Please verify your <pre>key=</pre> parameter.</body></html>")
        }

        if ("GetAppWishlistReporting" in url) {
            val app = q.getValue("appid").toLong()
            val date = LocalDate.parse(q.getValue("date"))
            val (a, d, p, g) = wishlists[app to date] ?: listOf(0, 0, 0, 0)
            val min = appMinDate[app]?.let { "\"$it\"" } ?: "null"
            return HttpResponse(
                200,
                """{"response":{"appid":$app,"date":"$date","wishlist_summary":{"wishlist_adds":$a,"wishlist_deletes":$d,"wishlist_purchases":$p,"wishlist_gifts":$g},"app_min_date":$min}}""",
            )
        }

        if ("GetChangedDatesForPartner" in url) {
            val since = q.getValue("highwatermark").toLong()
            highwatermarksSent.add(q.getValue("highwatermark"))
            val log = if (q["include_view_grants"] == "true") changes + grantedChanges else changes
            val changed = log.filter { it.first > since }
            val dates = changed.map { "\"${it.second.toString().replace('-', '/')}\"" }.distinct().joinToString(",")
            val next = changed.maxOfOrNull { it.first } ?: since
            return HttpResponse(200, """{"response":{"dates":[$dates],"result_highwatermark":"$next"}}""")
        }

        if ("GetDetailedSales" in url) {
            val date = LocalDate.parse(q.getValue("date"))
            salesDatesRequested.add(date)
            val after = q.getValue("highwatermark_id").toLong()
            val rows = sales[date].orEmpty() + if (q["include_view_grants"] == "true") grantedSales[date].orEmpty() else emptyList()

            // Satır kimlikleri 1'den başlıyor; sınırdan sonrakiler, sayfa boyu kadar.
            val page = rows.withIndex().map { (i, row) -> (i + 1L) to row }.filter { it.first > after }.take(pageSize)
            val maxId = page.lastOrNull()?.first ?: after
            val results = page.joinToString(",") { it.second }
            return HttpResponse(
                200,
                """{"response":{"results":[$results],"app_info":[{"appid":4000,"app_name":"Test Game"}],"max_id":"$maxId"}}""",
            )
        }

        return HttpResponse(404, "")
    }
}
