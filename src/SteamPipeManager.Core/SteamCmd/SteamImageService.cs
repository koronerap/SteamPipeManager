using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// Steam'in herkese açık uçlarından görsel çeker ve diske önbellekler.
/// API anahtarı gerekmez.
///
/// İki kaynak var:
///   * Oyun kapsülleri — güncel adresleri mağazanın <c>IStoreBrowseService/GetItems</c>
///     ucundan alınıyor. Steam yeni görselleri içerik özetli adreslere koyuyor
///     (<c>…/steam/apps/{appid}/{özet}/library_capsule.jpg?t=…</c>); eski sabit adres
///     (<c>…/steam/apps/{appid}/header.jpg</c>) güncellenmiyor, 2023'ten kalma görsel
///     verebiliyor ya da hiç yok. Mağazada görünmeyen app'lerde eski adreslere düşülüyor.
///   * Profil avatarı — <c>steamcommunity.com/profiles/{steamid64}?xml=1</c> içindeki
///     <c>avatarMedium</c>. Profil gizliyse veya yoksa yine yer tutucuya düşülür.
/// </summary>
public sealed partial class SteamImageService(string cacheDirectory, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private const string AssetHost = "https://shared.akamai.steamstatic.com/store_item_assets/";

    private const string LegacyHost = "https://cdn.cloudflare.steamstatic.com/steam/apps/";

    /// <summary>Mağazadaki güncel görsel adresleri; açılış başına app başına bir kez sorulur.</summary>
    private readonly ConcurrentDictionary<uint, Task<StoreAssets?>> _assets = new();

    /// <summary>Bu açılışta Steam'e sorulmuş görseller; ekranlar arası gezinmek yeniden sormasın.</summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _checked = new();

    public string CacheDirectory { get; } = cacheDirectory;

    /// <summary>Yatay mağaza kapsülü (460x215). Önbellekte olanı ağa çıkmadan döndürür.</summary>
    public string? GetCachedAppCapsule(uint steamAppId) => FindCached(CapsulePrefix(AssetKind.Header, steamAppId));

    /// <summary>
    /// Yatay mağaza kapsülü. Açılış başına bir kez Steam'e sorulur; değiştiyse indirilir.
    /// Bulunamazsa null döner.
    /// </summary>
    public Task<string?> GetAppCapsuleAsync(uint steamAppId, CancellationToken ct = default) =>
        GetCurrentAsync(AssetKind.Header, steamAppId, ct);

    /// <summary>
    /// Dikey kütüphane kapsülü (600x900), önbellekten ve ağa çıkmadan. Arayüz önce bunu
    /// gösteriyor, ardından <see cref="GetAppLibraryCapsuleAsync"/> ile tazeliyor.
    /// </summary>
    public string? GetCachedAppLibraryCapsule(uint steamAppId) => FindCached(CapsulePrefix(AssetKind.LibraryCapsule, steamAppId));

    /// <summary>
    /// Dikey kütüphane kapsülü (600x900). Steam kütüphanesindeki görünüm budur;
    /// oyun ve yan uygulama kartlarında kullanılır. Yoksa yatay kapsüle düşülmez —
    /// oranlar farklı olduğu için yer tutucu daha iyi görünüyor.
    ///
    /// Açılış başına bir kez Steam'e sorulur. Görsel değiştiyse yeni bir dosya adıyla
    /// indirilir: aynı yola yazılsaydı arayüzün görsel önbelleği eskisini göstermeye
    /// devam edebilirdi. Steam'e ulaşılamazsa önbellekteki görsel döner.
    /// </summary>
    public Task<string?> GetAppLibraryCapsuleAsync(uint steamAppId, CancellationToken ct = default) =>
        GetCurrentAsync(AssetKind.LibraryCapsule, steamAppId, ct);

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

    private enum AssetKind
    {
        Header,
        LibraryCapsule,
    }

    private static string CapsulePrefix(AssetKind kind, uint steamAppId) =>
        (kind == AssetKind.Header ? "app_" : "lib_") + steamAppId;

    private Task<string?> GetCurrentAsync(AssetKind kind, uint steamAppId, CancellationToken ct) =>
        _checked.GetOrAdd(CapsulePrefix(kind, steamAppId), _ => RefreshCapsuleAsync(kind, steamAppId, ct));

    private async Task<string?> RefreshCapsuleAsync(AssetKind kind, uint steamAppId, CancellationToken ct)
    {
        var prefix = CapsulePrefix(kind, steamAppId);
        var cached = FindCached(prefix);

        var assets = await GetStoreAssetsAsync(steamAppId, ct);

        if (assets is null)
        {
            // Steam'e ulaşılamadı: eldekiyle devam, bir sonraki açılışta yeniden sorulur.
            return cached;
        }

        string[] sources = (kind == AssetKind.Header ? assets.Header : assets.LibraryCapsule) is { } current
            ? [current]
            : LegacyUrls(kind, steamAppId);

        foreach (var url in sources)
        {
            var path = CachedPath($"{prefix}_{ShortHash(url)}.jpg");

            if (File.Exists(path))
            {
                // Değişmemiş; indirmeye gerek yok.
                RemoveOtherVersions(prefix, keep: path);
                return path;
            }

            if (await DownloadAsync(url, ct) is not { } bytes)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(path, bytes, ct);
            }
            catch (IOException)
            {
                return cached;
            }

            RemoveOtherVersions(prefix, keep: path);
            return path;
        }

        // Ne güncel ne eski adreste görsel var ya da indirilemedi: eldeki kalsın.
        return cached;
    }

    private static string[] LegacyUrls(AssetKind kind, uint steamAppId) => kind == AssetKind.Header
        ?
        [
            $"{LegacyHost}{steamAppId}/header.jpg",
            $"{LegacyHost}{steamAppId}/capsule_616x353.jpg",
        ]
        :
        [
            $"{LegacyHost}{steamAppId}/library_600x900.jpg",
            $"{LegacyHost}{steamAppId}/library_600x900_2x.jpg",
        ];

    /// <summary>Kapsül ve kütüphane görseli aynı yanıttan geliyor; app başına tek istek.</summary>
    private Task<StoreAssets?> GetStoreAssetsAsync(uint steamAppId, CancellationToken ct) =>
        _assets.GetOrAdd(steamAppId, id => FetchStoreAssetsAsync(id, ct));

    /// <summary>
    /// Mağazadaki güncel görsel adresleri. Ağ hatasında null; app mağazada görünmüyorsa
    /// adresleri boş bir sonuç (o zaman eski adresler deneniyor).
    /// </summary>
    private async Task<StoreAssets?> FetchStoreAssetsAsync(uint steamAppId, CancellationToken ct)
    {
        var input = """{"ids":[{"appid":""" + steamAppId +
                    """}],"context":{"language":"english","country_code":"US"},"data_request":{"include_assets":true}}""";
        var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input);

        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return StoreAssets.Parse(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private sealed record StoreAssets(string? Header, string? LibraryCapsule)
    {
        public static StoreAssets Parse(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("store_items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0 ||
                !items[0].TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Object ||
                Text(assets, "asset_url_format") is not { } format)
            {
                return new StoreAssets(null, null);
            }

            string? Url(string name) => Text(assets, name) is { } file
                ? AssetHost + format.Replace("${FILENAME}", file, StringComparison.Ordinal)
                : null;

            return new StoreAssets(Url("header"), Url("library_capsule"));
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text
                ? text
                : null;
    }

    /// <summary>
    /// Önbellekteki görsel. Dosya adı kaynak adresin özetini taşıyor
    /// (<c>lib_730_1a2b3c4d.jpg</c>); eski sürümlerin yazdığı <c>lib_730.jpg</c> de sayılıyor.
    /// </summary>
    private string? FindCached(string prefix)
    {
        if (!Directory.Exists(CacheDirectory))
        {
            return null;
        }

        var versioned = new DirectoryInfo(CacheDirectory)
            .EnumerateFiles($"{prefix}_*.jpg")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (versioned is not null)
        {
            return versioned.FullName;
        }

        var legacy = CachedPath($"{prefix}.jpg");
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>Yeni sürüm yazıldıktan sonra eskileri ve eski biçimdeki dosyaları siler.</summary>
    private void RemoveOtherVersions(string prefix, string keep)
    {
        try
        {
            var stale = new DirectoryInfo(CacheDirectory)
                .EnumerateFiles($"{prefix}_*.jpg")
                .Select(f => f.FullName)
                .Append(CachedPath($"{prefix}.jpg"))
                .Append(CachedPath($"{prefix}.jpg.missing"))
                .Where(path => !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase));

            foreach (var path in stale)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Arayüz eski dosyayı hâlâ açık tutuyor olabilir; bir sonraki açılışta silinir.
        }
    }

    private static string ShortHash(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12];

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
