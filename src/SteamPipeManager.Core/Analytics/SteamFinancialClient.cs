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

/// <summary>
/// Satış toplamı. Net = brüt − iade − vergi; Steam'in payı düşülmemiş.
/// </summary>
/// <param name="Units">Net adet: satılan eksi iade edilen.</param>
/// <param name="ReturnsUsd">İade edilen tutar (pozitif).</param>
/// <param name="TaxUsd">Steam'in topladığı vergi (pozitif).</param>
/// <param name="ShareUsd">
/// Tahmini ortak payı: her satırın neti × o günkü gelir payı (<see cref="SteamRevenueShare"/>).
/// Aylık rapordaki chargeback ve düzeltmeler dahil değil.
/// </param>
public sealed record SalesTotals(int Units, decimal NetUsd, decimal GrossUsd, decimal ReturnsUsd = 0, decimal TaxUsd = 0, decimal ShareUsd = 0)
{
    public static readonly SalesTotals Zero = new(0, 0m, 0m);

    public SalesTotals Add(SalesTotals other) =>
        new(Units + other.Units, NetUsd + other.NetUsd, GrossUsd + other.GrossUsd,
            ReturnsUsd + other.ReturnsUsd, TaxUsd + other.TaxUsd, ShareUsd + other.ShareUsd);

    /// <summary>Steam'in tahmini payı: net eksi ortak payı.</summary>
    public decimal SteamCutUsd => NetUsd - ShareUsd;
}

/// <summary>
/// Steam'in gelir payı: ilk 10 milyon dolar için %70, 10–50 milyon arası %75, üstü %80.
/// Eşiği Steam hesaplıyor; satış satırı o günün kademesini veriyor (0, 1, 2).
/// </summary>
public static class SteamRevenueShare
{
    public const decimal Base = 0.70m;

    public static decimal OfTier(int tier) => Base + 0.05m * Math.Clamp(tier, 0, 2);
}

public enum SalesLineKind
{
    /// <summary>Steam'de satılan paket (oyun, DLC, bundle içinde).</summary>
    Package,

    /// <summary>Steam dışında dağıtılan anahtarın etkinleştirilmesi; gelir yok.</summary>
    Retail,

    /// <summary>Oyun içi satış.</summary>
    MicroTxn,
}

/// <summary>
/// Steam'in bir satış satırı (<c>GetDetailedSales</c>). Steam satırları ülke, platform,
/// paket, indirim gibi alanlara göre zaten gruplanmış veriyor; toplamalar gösterirken
/// yapılıyor. Tutarlar dolar, fiyatlar satış para biriminin kuruşu.
/// </summary>
public sealed record SalesLine
{
    /// <summary>Gelirin yazıldığı app: pakette <c>primary_appid</c>, oyun içi satışta <c>appid</c>.</summary>
    public uint App { get; init; }

    public SalesLineKind Kind { get; init; }

    public uint Package { get; init; }

    public uint Bundle { get; init; }

    /// <summary>ISO 3166-1 alfa-2 ülke kodu.</summary>
    public string? Country { get; init; }

    public string? Platform { get; init; }

    public int Sold { get; init; }

    /// <summary>İade edilen adet (pozitif).</summary>
    public int Returned { get; init; }

    public int Activated { get; init; }

    public int NetUnits { get; init; }

    public decimal Gross { get; init; }

    /// <summary>İade tutarı (pozitif).</summary>
    public decimal Returns { get; init; }

    public decimal Tax { get; init; }

    public decimal Net { get; init; }

    /// <summary>Ek gelir payı kademesi: 0 yok, 1 +%5, 2 +%10.</summary>
    public int Tier { get; init; }

    public int Discount { get; init; }

    public int Item { get; init; }

    public uint KeyRequest { get; init; }

    public string? Currency { get; init; }

    public long BasePrice { get; init; }

    public long SalePrice { get; init; }

    /// <summary>
    /// Satırı paylaşan ortak (<c>view_grant_partnerid</c>): oyun yayıncının hesabındaysa
    /// satış onun, görme izni bizim. Kendi satışımızda 0.
    /// </summary>
    public uint ViewGrant { get; init; }

    public SalesTotals Totals => new(NetUnits, Net, Gross, Returns, Tax, Math.Round(Net * SteamRevenueShare.OfTier(Tier), 4));
}

public sealed record SalesDiscountInfo(string Name, int Percent);

public sealed record SalesCountryInfo(string Name, string Region);

/// <summary>Satış satırlarındaki numaraların adları; Steam her günün yanıtında veriyor.</summary>
public sealed class SalesLookups
{
    public Dictionary<uint, string> Packages { get; set; } = [];

    public Dictionary<uint, string> Bundles { get; set; } = [];

    /// <summary>Birleşik indirim (<c>combined_discount_id</c>): adı ve toplam oranı.</summary>
    public Dictionary<int, SalesDiscountInfo> Discounts { get; set; } = [];

    public Dictionary<string, SalesCountryInfo> Countries { get; set; } = [];

    /// <summary>Oyun içi ürünler, "app:ürün" anahtarıyla.</summary>
    public Dictionary<string, string> Items { get; set; } = [];

    /// <summary>Anahtar istekleri: Steamworks'teki notu.</summary>
    public Dictionary<uint, string> KeyRequests { get; set; } = [];

    public void Merge(SalesLookups other)
    {
        foreach (var (k, v) in other.Packages) Packages[k] = v;
        foreach (var (k, v) in other.Bundles) Bundles[k] = v;
        foreach (var (k, v) in other.Discounts) Discounts[k] = v;
        foreach (var (k, v) in other.Countries) Countries[k] = v;
        foreach (var (k, v) in other.Items) Items[k] = v;
        foreach (var (k, v) in other.KeyRequests) KeyRequests[k] = v;
    }
}

/// <summary>Bir günün satışları: app'e göre toplamlar, Steam'in satırları ve adları.</summary>
public sealed record SalesDay(
    DateOnly Date,
    IReadOnlyDictionary<uint, SalesTotals> ByApp,
    IReadOnlyDictionary<uint, string> AppNames,
    IReadOnlyList<SalesLine> Lines,
    SalesLookups Lookups);

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
    /// Başka bir ortağın (çoğunlukla yayıncının) paylaştığı veri de istensin. Oyun yayıncının
    /// hesabındaysa satışları orada; bu olmadan anahtar yalnızca kendi hesabındakileri (örneğin
    /// dağıtılan anahtarları) görüyor ve satışlar hiç gelmiyor.
    /// </summary>
    private const string ViewGrants = "include_view_grants=true";

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
            Url("GetChangedDatesForPartner", key, $"highwatermark={highwatermark}&{ViewGrants}"), ct).ConfigureAwait(false);

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
    /// Bir Pasifik günündeki satışlar: Steam'in satırları ve app'e göre toplamları. Sayfalar
    /// Valve'ın tarifiyle okunuyor: dönen <c>max_id</c> gönderilene eşit olana kadar.
    ///
    /// Paket satırları paketin bağlı olduğu app'e (<c>primary_appid</c>), oyun içi satışlar
    /// kendi <c>appid</c>'sine yazılıyor. Yalnızca etkinleştirme olan satırlar (anahtar
    /// dağıtımı) satır olarak saklanıyor ama gelir olmadığı için toplama girmiyor.
    /// </summary>
    public async Task<SalesDay> GetSalesDayAsync(string key, DateOnly date, CancellationToken ct = default)
    {
        var lines = new List<SalesLine>();
        var names = new Dictionary<uint, string>();
        var lookups = new SalesLookups();
        ulong cursor = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            using var json = await GetAsync(
                Url("GetDetailedSales", key, $"date={SteamWebApi.DateText(date)}&highwatermark_id={cursor}&{ViewGrants}"), ct).ConfigureAwait(false);

            var body = SteamWebApi.Body(json.RootElement);

            foreach (var row in Items(body, "results"))
            {
                lines.Add(LineOf(row));
            }

            foreach (var app in Items(body, "app_info"))
            {
                if (SteamWebApi.Long(app, "appid") is > 0 and var id && SteamWebApi.Text(app, "app_name") is { Length: > 0 } name)
                {
                    names[(uint)id] = name;
                }
            }

            ReadLookups(body, lookups);

            var maxId = SteamWebApi.ULong(body, "max_id");

            if (maxId <= cursor)
            {
                var byApp = new Dictionary<uint, SalesTotals>();

                foreach (var line in lines)
                {
                    var totals = line.Totals;

                    if (totals != SalesTotals.Zero)
                    {
                        byApp[line.App] = byApp.TryGetValue(line.App, out var existing) ? existing.Add(totals) : totals;
                    }
                }

                return new SalesDay(date, byApp, names, lines, lookups);
            }

            cursor = maxId;
        }

        throw new AnalyticsException(AnalyticsFailure.BadResponse, "Steam's sales data did not finish paging.");
    }

    private static IEnumerable<JsonElement> Items(JsonElement body, string name) =>
        body.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static SalesLine LineOf(JsonElement row)
    {
        var type = SteamWebApi.Text(row, "line_item_type");
        var kind = string.Equals(type, "MicroTxn", StringComparison.OrdinalIgnoreCase)
            ? SalesLineKind.MicroTxn
            : string.Equals(SteamWebApi.Text(row, "package_sale_type"), "Retail", StringComparison.OrdinalIgnoreCase)
                ? SalesLineKind.Retail
                : SalesLineKind.Package;

        var sold = (int)SteamWebApi.Long(row, "gross_units_sold");

        // İadeler raporda eksi işaretli gelebiliyor; burada hep pozitif.
        var returned = (int)Math.Abs(SteamWebApi.Long(row, "gross_units_returned"));

        return new SalesLine
        {
            App = (uint)SteamWebApi.Long(row, kind == SalesLineKind.MicroTxn ? "appid" : "primary_appid"),
            Kind = kind,
            Package = (uint)SteamWebApi.Long(row, "packageid"),
            Bundle = (uint)SteamWebApi.Long(row, "bundleid"),
            Country = SteamWebApi.Text(row, "country_code"),
            Platform = SteamWebApi.Text(row, "platform"),
            Sold = sold,
            Returned = returned,
            Activated = (int)SteamWebApi.Long(row, "gross_units_activated"),
            NetUnits = row.TryGetProperty("net_units_sold", out _) ? (int)SteamWebApi.Long(row, "net_units_sold") : sold - returned,
            Gross = SteamWebApi.Money(row, "gross_sales_usd"),
            Returns = Math.Abs(SteamWebApi.Money(row, "gross_returns_usd")),
            Tax = Math.Abs(SteamWebApi.Money(row, "net_tax_usd")),
            Net = SteamWebApi.Money(row, "net_sales_usd"),
            Tier = (int)SteamWebApi.Long(row, "additional_revenue_share_tier"),
            Discount = (int)SteamWebApi.Long(row, "combined_discount_id"),
            Item = (int)SteamWebApi.Long(row, "game_item_id"),
            KeyRequest = (uint)SteamWebApi.Long(row, "key_request_id"),
            Currency = SteamWebApi.Text(row, "currency"),
            BasePrice = SteamWebApi.Long(row, "base_price"),
            SalePrice = SteamWebApi.Long(row, "sale_price"),
            ViewGrant = (uint)SteamWebApi.Long(row, "view_grant_partnerid"),
        };
    }

    private static void ReadLookups(JsonElement body, SalesLookups lookups)
    {
        foreach (var p in Items(body, "package_info"))
        {
            if (SteamWebApi.Long(p, "packageid") is > 0 and var id && SteamWebApi.Text(p, "package_name") is { Length: > 0 } name)
            {
                lookups.Packages[(uint)id] = name;
            }
        }

        foreach (var b in Items(body, "bundle_info"))
        {
            if (SteamWebApi.Long(b, "bundleid") is > 0 and var id && SteamWebApi.Text(b, "bundle_name") is { Length: > 0 } name)
            {
                lookups.Bundles[(uint)id] = name;
            }
        }

        foreach (var d in Items(body, "combined_discount_info"))
        {
            if (SteamWebApi.Long(d, "combined_discount_id") is > 0 and var id)
            {
                lookups.Discounts[(int)id] = new SalesDiscountInfo(
                    SteamWebApi.Text(d, "combined_discount_name") ?? "", (int)SteamWebApi.Long(d, "total_discount_percentage"));
            }
        }

        foreach (var c in Items(body, "country_info"))
        {
            if (SteamWebApi.Text(c, "country_code") is { Length: > 0 } code)
            {
                lookups.Countries[code] = new SalesCountryInfo(SteamWebApi.Text(c, "country_name") ?? code, SteamWebApi.Text(c, "region") ?? "");
            }
        }

        foreach (var i in Items(body, "game_item_info"))
        {
            if (SteamWebApi.Long(i, "game_item_id") is > 0 and var item && SteamWebApi.Text(i, "game_item_description") is { Length: > 0 } description)
            {
                lookups.Items[$"{SteamWebApi.Long(i, "appid")}:{item}"] = description;
            }
        }

        foreach (var k in Items(body, "key_request_info"))
        {
            if (SteamWebApi.Long(k, "key_request_id") is > 0 and var id)
            {
                lookups.KeyRequests[(uint)id] = SteamWebApi.Text(k, "key_request_notes") ?? "";
            }
        }
    }
}
