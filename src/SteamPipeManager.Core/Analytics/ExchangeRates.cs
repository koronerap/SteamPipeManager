using System.Globalization;
using System.Text.Json;

namespace SteamPipeManager.Core.Analytics;

/// <summary>Bir para biriminin kur önbelleği: 1 doların karşılığı, günlere (<c>yyyy-MM-dd</c>) göre.</summary>
public sealed class ExchangeRateLedger
{
    public Dictionary<string, decimal> Days { get; set; } = [];

    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>
/// 1 doların bir para birimindeki karşılığı, gün gün. Kuru olmayan günde (yayın yok, henüz
/// gelmedi) en yakın önceki günün kuru.
/// </summary>
public sealed class ExchangeRates(string currency, IReadOnlyDictionary<DateOnly, decimal> days)
{
    /// <summary>Bir günün kuru en çok bu kadar gün önceki kurla tamamlanıyor.</summary>
    private const int LookBack = 14;

    public string Currency { get; } = currency;

    public int Count => days.Count;

    public decimal? On(DateOnly date)
    {
        for (var day = date; day >= date.AddDays(-LookBack); day = day.AddDays(-1))
        {
            if (days.TryGetValue(day, out var rate))
            {
                return rate;
            }
        }

        return null;
    }

    public decimal? Latest => days.Count == 0 ? null : days[days.Keys.Max()];

    /// <summary>Gün gün çevrilmiş toplam; o günün kuru yoksa null (yarım toplam yanıltır).</summary>
    public decimal? Convert(IEnumerable<(DateOnly Date, decimal Usd)> amounts)
    {
        decimal total = 0;

        foreach (var (date, usd) in amounts)
        {
            if (usd == 0)
            {
                continue;
            }

            if (On(date) is not { } rate)
            {
                return null;
            }

            total += usd * rate;
        }

        return total;
    }
}

/// <summary>
/// Frankfurter (frankfurter.dev): merkez bankalarının günlük kurları, anahtarsız. İstekte
/// yalnızca para birimi ve tarih aralığı gidiyor; hiçbir satış verisi gönderilmiyor.
/// </summary>
public sealed class ExchangeRateClient(HttpClient http, string host = ExchangeRateClient.DefaultHost)
{
    public const string DefaultHost = "https://api.frankfurter.dev/";

    public async Task<IReadOnlyDictionary<DateOnly, decimal>> GetSeriesAsync(
        string currency, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var url = $"{host}v2/rates?base=USD&quotes={Uri.EscapeDataString(currency)}" +
                  $"&from={SteamWebApi.DateText(from)}&to={SteamWebApi.DateText(to)}";

        using var json = await SteamWebApi.GetJsonAsync(http, url, ct).ConfigureAwait(false)
                         ?? throw new AnalyticsException(AnalyticsFailure.BadResponse, "No exchange rates came back.");

        var result = new Dictionary<DateOnly, decimal>();

        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new AnalyticsException(AnalyticsFailure.BadResponse, "Exchange rates came back in an unexpected form.");
        }

        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (SteamWebApi.Date(SteamWebApi.Text(item, "date")) is { } date &&
                item.TryGetProperty("rate", out var rate) && rate.ValueKind == JsonValueKind.Number &&
                rate.TryGetDecimal(out var value) && value > 0)
            {
                result[date] = value;
            }
        }

        return result;
    }
}

/// <summary>
/// Kurları önbellekten verir; eksik geçmiş bir istekle, yeni günler en çok altı saatte bir
/// iniyor. Ağ yoksa eldekiyle devam ediliyor.
/// </summary>
public sealed class ExchangeRateService(AnalyticsStore store, ExchangeRateClient client, TimeProvider? time = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>Ulaşılamadıktan sonra bu kadar süre yeniden denenmiyor; her yenilemede beklenmesin.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _quietUntil;

    /// <summary>
    /// <paramref name="currency"/> için <paramref name="from"/>'dan bugüne kurlar. Dolar
    /// istenirse çeviri gerekmiyor: null.
    /// </summary>
    public async Task<ExchangeRates?> GetAsync(string currency, DateOnly from, CancellationToken ct = default)
    {
        currency = currency.ToUpperInvariant();

        if (currency == "USD")
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var ledger = await store.LoadRatesAsync(currency, ct).ConfigureAwait(false);
        var known = Parse(ledger);

        if (now < _quietUntil)
        {
            return new ExchangeRates(currency, known);
        }

        try
        {
            var changed = false;
            var earliest = known.Count > 0 ? known.Keys.Min() : (DateOnly?)null;

            if (earliest is null || from < earliest)
            {
                Merge(known, await client.GetSeriesAsync(currency, from, earliest?.AddDays(-1) ?? today, ct).ConfigureAwait(false));
                changed = true;
            }

            var latest = known.Count > 0 ? known.Keys.Max() : (DateOnly?)null;

            if (latest is { } last && last < today && (ledger.LastCheckedAt is not { } checkedAt || now - checkedAt >= RefreshInterval))
            {
                Merge(known, await client.GetSeriesAsync(currency, last.AddDays(1), today, ct).ConfigureAwait(false));
                changed = true;
            }

            if (changed)
            {
                ledger.Days = known.ToDictionary(p => SteamWebApi.DateText(p.Key), p => p.Value);
                ledger.LastCheckedAt = now;
                await store.SaveRatesAsync(currency, ledger, ct).ConfigureAwait(false);
            }
        }
        catch (AnalyticsException)
        {
            // Kur servisine ulaşılamadı: önbellekteki kurlarla (varsa) devam.
            _quietUntil = now + RetryAfterFailure;
        }

        return new ExchangeRates(currency, known);
    }

    private static Dictionary<DateOnly, decimal> Parse(ExchangeRateLedger ledger) =>
        ledger.Days
            .Select(p => (Date: SteamWebApi.Date(p.Key), Rate: p.Value))
            .Where(p => p.Date is not null)
            .ToDictionary(p => p.Date!.Value, p => p.Rate);

    private static void Merge(Dictionary<DateOnly, decimal> known, IReadOnlyDictionary<DateOnly, decimal> fetched)
    {
        foreach (var (date, rate) in fetched)
        {
            known[date] = rate;
        }
    }
}

/// <summary>Para birimi kodları ve sembolleri (ör. TRY → ₺).</summary>
public static class Currencies
{
    /// <summary>Ayarlarda sunulanlar; Steam'in en çok sattığı ve kur servisinin verdiği birimler.</summary>
    public static readonly IReadOnlyList<string> Offered =
        ["USD", "EUR", "GBP", "TRY", "JPY", "KRW", "CNY", "RUB", "BRL", "CAD", "AUD", "PLN", "INR", "CHF", "MXN", "UAH", "KZT"];

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["TRY"] = "₺", ["JPY"] = "¥", ["KRW"] = "₩",
        ["CNY"] = "CN¥", ["RUB"] = "₽", ["BRL"] = "R$", ["CAD"] = "CA$", ["AUD"] = "A$", ["PLN"] = "zł ",
        ["INR"] = "₹", ["CHF"] = "CHF ", ["MXN"] = "MX$", ["UAH"] = "₴", ["KZT"] = "₸",
    };

    /// <summary>Tutarın önüne yazılan sembol; tanınmayan birimde kodun kendisi ("SEK ").</summary>
    public static string SymbolOf(string code) => Symbols.TryGetValue(code, out var symbol) ? symbol : code.ToUpperInvariant() + " ";

    /// <summary>
    /// Kullanıcının bulunduğu ülkenin para birimi; bilinmiyorsa null. Windows'ta biçim
    /// ayarından (birçok kullanıcıda en-US) değil "Ülke veya bölge" ayarından.
    /// </summary>
    public static string? OfHomeRegion()
    {
        var region = OperatingSystem.IsWindows() ? WindowsHomeRegion() : null;

        try
        {
            return new RegionInfo(region ?? CultureInfo.CurrentCulture.Name).ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsHomeRegion()
    {
        try
        {
            var buffer = new char[16];
            var length = GetUserDefaultGeoName(buffer, buffer.Length);
            return length > 1 ? new string(buffer, 0, length - 1) : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Eski Windows: biçim ayarına düşülüyor.
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName(char[] geoName, int geoNameCount);
}
