namespace SteamPipeManager.Core.Analytics;

/// <summary>
/// Anlık oyuncu sayısı: Steam'in herkese açık <c>GetNumberOfCurrentPlayers</c> ucu,
/// anahtar istemiyor. Steam geçmiş vermiyor; zirve ve grafik uygulamanın kendi
/// aldığı örneklerden çıkıyor (bkz. <see cref="AnalyticsService"/>).
/// </summary>
public sealed class SteamPlayerCountClient(HttpClient http)
{
    /// <summary>
    /// Şu anki oyuncu sayısı. Steam'de verisi olmayan app'lerde (yayınlanmamış, bilinmeyen)
    /// null; Steam 404 ve <c>"result": 42</c> döndürüyor.
    /// </summary>
    public async Task<int?> GetCurrentPlayersAsync(uint appId, CancellationToken ct = default)
    {
        using var json = await SteamWebApi.GetJsonAsync(
            http,
            $"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}",
            ct,
            notFoundIsEmpty: true).ConfigureAwait(false);

        if (json is null)
        {
            return null;
        }

        var body = SteamWebApi.Body(json.RootElement);

        return SteamWebApi.Long(body, "result") == 1
            ? (int)SteamWebApi.Long(body, "player_count")
            : null;
    }
}
