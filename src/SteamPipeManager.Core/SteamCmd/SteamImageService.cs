using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// Steam'in herkese açık uçlarından görsel çeker ve diske önbellekler.
/// API anahtarı gerekmez.
///
/// İki kaynak var:
///   * Oyun kapsülü — <c>cdn.cloudflare.steamstatic.com/steam/apps/{appid}/header.jpg</c>.
///     Henüz yayınlanmamış app'lerde 404 döner; o zaman yer tutucu kullanılır.
///   * Profil avatarı — <c>steamcommunity.com/profiles/{steamid64}?xml=1</c> içindeki
///     <c>avatarMedium</c>. Profil gizliyse veya yoksa yine yer tutucuya düşülür.
/// </summary>
public sealed partial class SteamImageService(string cacheDirectory, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Bulunamayan görseller için işaret dosyası; her açılışta tekrar denenmesin.</summary>
    private const string MissingMarker = ".missing";

    /// <summary>İşaret dosyası bu süreden eskiyse yeniden denenir (oyun sonradan yayınlanabilir).</summary>
    public TimeSpan MissingRetryAfter { get; set; } = TimeSpan.FromDays(1);

    public string CacheDirectory { get; } = cacheDirectory;

    /// <summary>Yatay mağaza kapsülü (460x215). Bulunamazsa null döner.</summary>
    public Task<string?> GetAppCapsuleAsync(uint steamAppId, CancellationToken ct = default) =>
        GetCachedAsync(
            $"app_{steamAppId}.jpg",
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/capsule_616x353.jpg",
            ],
            ct);

    /// <summary>
    /// Dikey kütüphane kapsülü (600x900). Steam kütüphanesindeki görünüm budur;
    /// oyun ve yan uygulama kartlarında kullanılır. Yoksa yatay kapsüle düşülmez —
    /// oranlar farklı olduğu için yer tutucu daha iyi görünüyor.
    /// </summary>
    public Task<string?> GetAppLibraryCapsuleAsync(uint steamAppId, CancellationToken ct = default) =>
        GetCachedAsync(
            $"lib_{steamAppId}.jpg",
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/library_600x900.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/library_600x900_2x.jpg",
            ],
            ct);

    /// <summary>
    /// Önbellekteki avatarı ağa hiç çıkmadan döndürür. Arayüz açılışta bunu kullanır;
    /// böylece profil ekranına her gelişte görsel yeniden yüklenmez.
    /// </summary>
    public string? GetCachedAvatar(ulong steamId64)
    {
        var path = CachedPath($"avatar_{steamId64}.jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Avatarı indirir. Önbellekte varsa doğrudan onu döndürür.
    /// </summary>
    public Task<string?> GetAvatarAsync(ulong steamId64, CancellationToken ct = default) =>
        GetCachedAvatar(steamId64) is { } cached
            ? Task.FromResult<string?>(cached)
            : RefreshAvatarAsync(steamId64, ct);

    /// <summary>
    /// Steam'den avatarı yeniden çeker ve değiştiyse önbelleği günceller.
    /// İndirme başarısız olursa <b>mevcut önbellek korunur</b> — kullanıcı görselini kaybetmez.
    /// Uygulama açılışında profil başına bir kez çağrılır.
    /// </summary>
    public async Task<string?> RefreshAvatarAsync(ulong steamId64, CancellationToken ct = default)
    {
        var path = CachedPath($"avatar_{steamId64}.jpg");
        var avatarUrl = await ResolveAvatarUrlAsync(steamId64, ct);

        if (avatarUrl is null)
        {
            return GetCachedAvatar(steamId64);
        }

        var downloaded = await DownloadAsync(avatarUrl, ct);

        if (downloaded is null)
        {
            return GetCachedAvatar(steamId64);
        }

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(path, downloaded, ct);
        }
        catch (IOException)
        {
            // Yazılamazsa eldeki önbellek kullanılmaya devam edilir.
        }

        return File.Exists(path) ? path : null;
    }

    private async Task<string?> ResolveAvatarUrlAsync(ulong steamId64, CancellationToken ct)
    {
        try
        {
            var xml = await _http.GetStringAsync(
                $"https://steamcommunity.com/profiles/{steamId64}?xml=1", ct);

            return AvatarPattern().Match(xml) is { Success: true } match
                ? match.Groups["url"].Value
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> GetCachedAsync(string fileName, string[] urls, CancellationToken ct)
    {
        var path = CachedPath(fileName);

        if (File.Exists(path))
        {
            return path;
        }

        if (IsMarkedMissing(path))
        {
            return null;
        }

        Directory.CreateDirectory(CacheDirectory);

        foreach (var url in urls)
        {
            if (await DownloadAsync(url, ct) is not { } bytes)
            {
                continue;
            }

            try
            {
                await File.WriteAllBytesAsync(path, bytes, ct);
                return path;
            }
            catch (IOException)
            {
                return null;
            }
        }

        MarkMissing(path);
        return null;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Boş ya da bozuk yanıtı önbelleğe almayalım.
            return bytes.Length >= 512 ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private string CachedPath(string fileName) => Path.Combine(CacheDirectory, fileName);

    private bool IsMarkedMissing(string path)
    {
        var marker = path + MissingMarker;

        return File.Exists(marker) &&
               DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < MissingRetryAfter;
    }

    private void MarkMissing(string path)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(path + MissingMarker, "");
        }
        catch (IOException)
        {
            // Önbellek işareti yazılamazsa sadece bir sonraki açılışta tekrar denenir.
        }
    }

    [GeneratedRegex(@"<avatarMedium><!\[CDATA\[(?<url>[^\]]+)\]\]></avatarMedium>")]
    private static partial Regex AvatarPattern();
}

/// <summary>SteamCMD'nin bildirdiği hesap numarasını SteamID64'e çevirir.</summary>
public static class SteamIdUtil
{
    /// <summary>Bireysel hesapların SteamID64 tabanı.</summary>
    public const ulong IndividualBase = 76561197960265728;

    public static ulong ToSteamId64(uint accountId) => IndividualBase + accountId;

    public static uint ToAccountId(ulong steamId64) =>
        steamId64 > IndividualBase ? (uint)(steamId64 - IndividualBase) : 0;
}
