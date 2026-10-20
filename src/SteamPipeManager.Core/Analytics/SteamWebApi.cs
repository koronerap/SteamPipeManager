using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SteamPipeManager.Core.Analytics;

/// <summary>Analitik verisinin neden alınamadığı; arayüz kullanıcıya bunu söylüyor.</summary>
public enum AnalyticsFailure
{
    /// <summary>Steam'e ulaşılamadı ya da sunucu hata verdi; sonraki yenilemede yeniden denenir.</summary>
    Network,

    /// <summary>Steam anahtarı reddetti (HTTP 401/403). Yeniden denemek işe yaramaz.</summary>
    InvalidKey,

    /// <summary>Steam istekleri sınırladı (HTTP 429).</summary>
    RateLimited,

    /// <summary>Yanıt beklenen biçimde değil.</summary>
    BadResponse,
}

/// <summary>
/// Steam Web API hatası. Mesaj istek adresini <b>içermiyor</b>: finans anahtarı adreste
/// gidiyor ve hata metinleri arayüzde, log'larda görünebiliyor.
/// </summary>
public sealed class AnalyticsException(AnalyticsFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AnalyticsFailure Failure { get; } = failure;
}

/// <summary>Steam Web API çağrıları için ortak yardımcılar.</summary>
internal static class SteamWebApi
{
    /// <summary>
    /// GET isteği atar ve JSON'u döndürür. <paramref name="notFoundIsEmpty"/> verilirse
    /// 404 hata sayılmıyor, null dönüyor (ör. oyuncu sayısı olmayan app'ler).
    /// </summary>
    public static async Task<JsonDocument?> GetJsonAsync(
        HttpClient http, string url, CancellationToken ct, bool notFoundIsEmpty = false)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AnalyticsException(AnalyticsFailure.Network, "Steam could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AnalyticsException(AnalyticsFailure.Network, "Steam did not answer in time.", ex);
        }

        using (response)
        {
            if (notFoundIsEmpty && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AnalyticsException(AnalyticsFailure.InvalidKey, "Steam rejected the key.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AnalyticsException(AnalyticsFailure.RateLimited, "Steam is limiting requests; trying again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AnalyticsException(
                    AnalyticsFailure.Network, $"Steam answered with HTTP {(int)response.StatusCode}.");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new AnalyticsException(AnalyticsFailure.BadResponse, "Steam's answer could not be read.", ex);
            }
        }
    }

    /// <summary>Yanıtlar <c>{"response": {...}}</c> içinde geliyor; yoksa kökün kendisi.</summary>
    public static JsonElement Body(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var body) ? body : root;

    // Valve'ın örneklerinde aynı türden sayılar kimi yerde tırnaklı, kimi yerde değil
    // ("partnerid": "10500" ama "packageid": 123); ikisi de okunuyor.

    public static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    public static ulong ULong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>Para alanları dört ondalıklı metin olarak geliyor: <c>"311.7201"</c>.</summary>
    public static decimal Money(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m,
        };
    }

    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd"];

    public static DateOnly? Date(string? text) =>
        text is { Length: > 0 } &&
        DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
