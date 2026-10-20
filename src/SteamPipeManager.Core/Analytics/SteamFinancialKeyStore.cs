using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Analytics;

/// <summary>
/// Steam finans anahtarının (Financial API Group) deposu. Anahtar ortağın bütün satış
/// ve wishlist verisini görüyor; Epic secret'ları gibi sistemin deposunda duruyor,
/// <c>profiles.json</c>'a ya da ayar dosyasına hiç yazılmıyor. Ana uygulama ve widget
/// aynı depoyu okuyor.
/// </summary>
public sealed class SteamFinancialKeyStore
{
    public static readonly SecretScope Scope = new(
        "SteamPipeManager.Steam.FinancialApiKey", "Steam Pipe Manager — Steam Financial API", "financial");

    private const string Key = "steam-financial-api-key";

    private readonly ISecretBackend _backend;

    /// <param name="filePath">Dosya tabanlı depoların (DPAPI, 0600) kullandığı dosya.</param>
    /// <param name="backend">Testler için; verilmezse platformunki.</param>
    public SteamFinancialKeyStore(string filePath, ISecretBackend? backend = null, HostPlatform? platform = null)
    {
        _backend = backend ?? SecretStorage.CreateDefault(filePath, platform ?? HostPlatform.Current, Scope);
    }

    public SecretStorageKind Kind => _backend.Kind;

    public bool IsEncrypted => Kind != SecretStorageKind.OwnerOnlyFile;

    public bool HasKey => _backend.Has(Key);

    /// <summary>Anahtar; yoksa ya da çözülemiyorsa null.</summary>
    public string? Read() => _backend.Read(Key);

    /// <summary>Anahtarı yazar; boş değer siler. Kopyalarken gelen boşluklar atılıyor.</summary>
    public void Write(string? key)
    {
        if (key?.Trim() is not { Length: > 0 } trimmed)
        {
            _backend.Delete(Key);
            return;
        }

        _backend.Write(Key, trimmed);
    }
}
