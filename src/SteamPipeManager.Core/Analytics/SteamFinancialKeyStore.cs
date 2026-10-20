using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Analytics;

/// <summary>
/// Steam finans anahtarlarının (Financial API Group) deposu, profil başına. Anahtar bir
/// Steamworks ortağının (şirketin) bütün satış ve wishlist verisini görüyor, başka
/// şirketinkini görmüyor; farklı şirketlere ait profillerin kendi anahtarı olmalı.
///
/// Epic secret'ları gibi sistemin deposunda duruyor, <c>profiles.json</c>'a ya da ayar
/// dosyasına hiç yazılmıyor. Ana uygulama ve widget aynı depoyu okuyor.
/// </summary>
public sealed class SteamFinancialKeyStore
{
    public static readonly SecretScope Scope = new(
        "SteamPipeManager.Steam.FinancialApiKey", "Steam Pipe Manager — Steam Financial API", "financial");

    /// <summary>İlk sürümün profile bağlı olmayan tek anahtarı; taşınana ya da silinene kadar duruyor.</summary>
    private const string LegacyName = "steam-financial-api-key";

    /// <summary>
    /// Okunan anahtarlar bir süre bellekte: yenileme döngüsü her dakika soruyor, macOS ve
    /// Linux'ta her okuma bir süreç. Widget ayrı süreç; ana uygulamada değişen anahtarı en
    /// geç bu süre sonra görüyor.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly ISecretBackend _backend;
    private readonly ConcurrentDictionary<Guid, (string? Value, DateTimeOffset ReadAt)> _cache = new();

    /// <param name="filePath">Dosya tabanlı depoların (DPAPI, 0600) kullandığı dosya.</param>
    /// <param name="backend">Testler için; verilmezse platformunki.</param>
    public SteamFinancialKeyStore(string filePath, ISecretBackend? backend = null, HostPlatform? platform = null)
    {
        _backend = backend ?? SecretStorage.CreateDefault(filePath, platform ?? HostPlatform.Current, Scope);
    }

    public SecretStorageKind Kind => _backend.Kind;

    public bool IsEncrypted => Kind != SecretStorageKind.OwnerOnlyFile;

    private static string NameOf(Guid profileId) => $"{LegacyName}:{profileId:N}";

    public bool Has(Guid profileId) => Read(profileId) is not null;

    /// <summary>Profilin anahtarı; yoksa ya da çözülemiyorsa null.</summary>
    public string? Read(Guid profileId)
    {
        if (_cache.TryGetValue(profileId, out var cached) && DateTimeOffset.UtcNow - cached.ReadAt < CacheFor)
        {
            return cached.Value;
        }

        var value = _backend.Read(NameOf(profileId));
        _cache[profileId] = (value, DateTimeOffset.UtcNow);
        return value;
    }

    /// <summary>Anahtarı yazar; boş değer siler. Kopyalarken gelen boşluklar atılıyor.</summary>
    public void Write(Guid profileId, string? key)
    {
        var trimmed = key?.Trim();

        if (trimmed is not { Length: > 0 })
        {
            _backend.Delete(NameOf(profileId));
            _cache[profileId] = (null, DateTimeOffset.UtcNow);
            return;
        }

        _backend.Write(NameOf(profileId), trimmed);
        _cache[profileId] = (trimmed, DateTimeOffset.UtcNow);
    }

    /// <summary>Profil silinince anahtarı da gidiyor.</summary>
    public void Remove(Guid profileId) => Write(profileId, null);

    /// <summary>İlk sürümden kalan, profile bağlı olmayan anahtar var mı.</summary>
    public bool HasLegacy => _backend.Has(LegacyName);

    /// <summary>Eski anahtarı bir profile taşır; hangi şirkete ait olduğunu kullanıcı biliyor.</summary>
    public void MoveLegacyTo(Guid profileId)
    {
        if (_backend.Read(LegacyName) is { } legacy)
        {
            Write(profileId, legacy);
            _backend.Delete(LegacyName);
        }
    }

    public void DeleteLegacy() => _backend.Delete(LegacyName);

    /// <summary>
    /// Anahtarın kimliği; anahtarın kendisi yerine dosya adlarında ve durumda kullanılıyor.
    /// Aynı şirketin iki profiline aynı anahtar girilirse ikisi aynı kimliği alıyor ve
    /// satış verisi bir kez indiriliyor.
    /// </summary>
    public static string Fingerprint(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))[..16];
}
