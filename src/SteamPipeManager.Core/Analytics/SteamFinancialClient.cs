using System.Text.Json;

namespace SteamPipeManager.Core.Analytics;

/// <summary>Bir günün wishlist hareketleri (GMT günü).</summary>
public sealed record WishlistDay(DateOnly Date, int Adds, int Deletes, int Purchases, int Gifts, DateOnly? AppMinDate)
{
    /// <summary>Wishlist'te kalan sayıya etkisi: eklenen, eksi silinen, satın alınan ve hediye edilen.</summary>
    public int Net => Adds - Deletes - Purchases - Gifts;
}

/// <summary>Yeni ya da yeniden hesaplanmış satış verisi olan günler (Pasifik saati).</summary>
public sealed record ChangedDates(IReadOnlyList<DateOnly> Dates, ulong Highwatermark);

/// <summary>Satış toplamı. Gelir Steam'in payı düşülmeden; iade ve vergiler düşülmüş.</summary>
public sealed record SalesTotals(int Units, decimal NetUsd, decimal GrossUsd)
{
    public static readonly SalesTotals Zero = new(0, 0m, 0m);

    public SalesTotals Add(SalesTotals other) =>
        new(Units + other.Units, NetUsd + other.NetUsd, GrossUsd + other.GrossUsd);
}

/// <summary>Bir günün satışları, app'e göre toplanmış.</summary>
public sealed record SalesDay(
    DateOnly Date,
    IReadOnlyDictionary<uint, SalesTotals> ByApp,
    IReadOnlyDictionary<uint, string> AppNames);

/// <summary>
/// Steam'in yayıncıya özel finans servisi (<c>IPartnerFinancialsService</c>). Ayrı bir
/// "Financial API Group" anahtarı istiyor; anahtar ortağın <b>bütün</b> uygulamalarının
/// satış ve wishlist verisini görüyor, bu yüzden parola gibi saklanıyor.
///
/// Anahtar adresin içinde gidiyor (Valve'ın tarif ettiği yol); hata mesajları bu yüzden
/// adres içermiyor (bkz. <see cref="AnalyticsException"/>).
/// </summary>
public sealed class SteamFinancialClient(HttpClient http, string host = SteamFinancialClient.DefaultHost)
{
    /// <summary>Yayıncı uçları için Valve'ın önerdiği, daha yüksek erişilebilirlikli adres.</summary>
    public const string DefaultHost = "https://partner.steam-api.com/";

    private const string Service = "IPartnerFinancialsService";

    /// <summary>Sayfalamanın sonsuza dek sürmemesi için üst sınır.</summary>
    private const int MaxPages = 1000;

    /// <summary>
    /// Her istekten önce çağrılıyor; çağıran istek sayısını ve hızını buradan yönetiyor
    /// (bkz. <see cref="AnalyticsService"/>).
    /// </summary>
    public Func<CancellationToken, Task>? BeforeRequest { get; set; }

    private string Url(string method, string key, string query) =>
        $"{host}{Service}/{method}/v001/?key={Uri.EscapeDataString(key)}&{query}";

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        if (BeforeRequest is { } gate)
        {
            await gate(ct).ConfigureAwait(false);
        }

        return await SteamWebApi.GetJsonAsync(http, url, ct).ConfigureAwait(false)
               ?? throw new AnalyticsException(AnalyticsFailure.BadResponse, "Steam returned no data.");
    }

    /// <summary>Bir app'in bir GMT günündeki wishlist hareketleri.</summary>
    public async Task<WishlistDay> GetWishlistDayAsync(string key, uint appId, DateOnly date, CancellationToken ct = default)
    {
        using var json = await GetAsync(
            Url("GetAppWishlistReporting", key, $"appid={appId}&date={SteamWebApi.DateText(date)}"), ct).ConfigureAwait(false);

        var body = SteamWebApi.Body(json.RootElement);

        var summary = body.TryGetProperty("wishlist_summary", out var s) && s.ValueKind == JsonValueKind.Object
            ? s
            : default;

        int Count(string name) => summary.ValueKind == JsonValueKind.Object ? (int)SteamWebApi.Long(summary, name) : 0;

        return new WishlistDay(
            date,
            Count("wishlist_adds"),
            Count("wishlist_deletes"),
            Count("wishlist_purchases"),
            Count("wishlist_gifts"),
            SteamWebApi.Date(SteamWebApi.Text(body, "app_min_date")));
    }

    /// <summary>
    /// Son sorgudan bu yana verisi değişen günler. İlk çağrıda 0 verilir ve verisi olan
    /// bütün günler gelir; dönen sınır saklanıp bir sonrakinde veriliyor.
    /// </summary>
    public async Task<ChangedDates> GetChangedDatesAsync(string key, ulong highwatermark, CancellationToken ct = default)
    {
        using var json = await GetAsync(
            Url("GetChangedDatesForPartner", key, $"highwatermark={highwatermark}"), ct).ConfigureAwait(false);

        var body = SteamWebApi.Body(json.RootElement);
        var dates = new List<DateOnly>();

        if (body.TryGetProperty("dates", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && SteamWebApi.Date(item.GetString()) is { } date)
                {
                    dates.Add(date);
                }
            }
        }

        var next = SteamWebApi.ULong(body, "result_highwatermark");
        return new ChangedDates(dates, next == 0 ? highwatermark : next);
    }

    /// <summary>
    /// Bir Pasifik günündeki satışlar, app'e göre toplanmış. Sayfalar Valve'ın tarifiyle
    /// okunuyor: dönen <c>max_id</c> gönderilene eşit olana kadar.
    ///
    /// Paket satırları paketin bağlı olduğu app'e (<c>primary_appid</c>), oyun içi satışlar
    /// kendi <c>appid</c>'sine yazılıyor. Yalnızca etkinleştirme olan satırlar (anahtar
    /// dağıtımı) satış değil, sayılmıyor.
    /// </summary>
    public async Task<SalesDay> GetSalesDayAsync(string key, DateOnly date, CancellationToken ct = default)
    {
        var byApp = new Dictionary<uint, SalesTotals>();
        var names = new Dictionary<uint, string>();
        ulong cursor = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            using var json = await GetAsync(
                Url("GetDetailedSales", key, $"date={SteamWebApi.DateText(date)}&highwatermark_id={cursor}"), ct).ConfigureAwait(false);

            var body = SteamWebApi.Body(json.RootElement);

            if (body.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in results.EnumerateArray())
                {
                    AddRow(row, byApp);
                }
            }

            if (body.TryGetProperty("app_info", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var app in apps.EnumerateArray())
                {
                    if (SteamWebApi.Long(app, "appid") is > 0 and var id && SteamWebApi.Text(app, "app_name") is { Length: > 0 } name)
                    {
                        names[(uint)id] = name;
                    }
                }
            }

            var maxId = SteamWebApi.ULong(body, "max_id");

            if (maxId <= cursor)
            {
                return new SalesDay(date, byApp, names);
            }

            cursor = maxId;
        }

        throw new AnalyticsException(AnalyticsFailure.BadResponse, "Steam's sales data did not finish paging.");
    }

    private static void AddRow(JsonElement row, Dictionary<uint, SalesTotals> byApp)
    {
        var isMicroTxn = string.Equals(SteamWebApi.Text(row, "line_item_type"), "MicroTxn", StringComparison.OrdinalIgnoreCase);
        var appId = (uint)SteamWebApi.Long(row, isMicroTxn ? "appid" : "primary_appid");

        var units = row.TryGetProperty("net_units_sold", out _)
            ? (int)SteamWebApi.Long(row, "net_units_sold")
            : (int)(SteamWebApi.Long(row, "gross_units_sold") + SteamWebApi.Long(row, "gross_units_returned"));

        var totals = new SalesTotals(units, SteamWebApi.Money(row, "net_sales_usd"), SteamWebApi.Money(row, "gross_sales_usd"));

        if (totals == SalesTotals.Zero)
        {
            return;
        }

        byApp[appId] = byApp.TryGetValue(appId, out var existing) ? existing.Add(totals) : totals;
    }
}
