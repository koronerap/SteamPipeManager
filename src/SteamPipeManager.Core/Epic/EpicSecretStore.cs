using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Epic client secret'larının yerel deposu.
///
/// Neden profil dosyasının dışında: <c>profiles.json</c>'ın yedeklenebilir ve
/// paylaşılabilir kalması Steam tarafında verdiğimiz bir sözdü ve README'de yazıyor.
/// Epic'te secret'ın kalıcı olması gerekiyor (Steam'deki gibi aracın hatırladığı bir
/// oturum yok), ama bu sözü bozmanın gerekçesi olamaz.
///
/// Nerede durduğu platforma göre değişiyor (bkz. <see cref="SecretStorageKind"/>):
/// Windows'ta DPAPI ile şifreli dosya, macOS'ta Anahtar Zinciri, Linux'ta masaüstünün
/// gizli bilgi servisi; o da yoksa yalnızca sahibinin okuyabildiği bir dosya.
/// </summary>
public sealed class EpicSecretStore
{
    private readonly ISecretBackend _backend;

    /// <param name="filePath">Dosya tabanlı depoların (DPAPI, 0600) kullandığı dosya.</param>
    /// <param name="backend">Testler ya da özel kurulumlar için; verilmezse platformunki.</param>
    public EpicSecretStore(string filePath, ISecretBackend? backend = null, HostPlatform? platform = null)
    {
        FilePath = filePath;
        _backend = backend ?? CreateDefault(filePath, platform ?? HostPlatform.Current);
    }

    public string FilePath { get; }

    public SecretStorageKind Kind => _backend.Kind;

    /// <summary>Secret şifreli mi duruyor; değilse arayüz kullanıcıyı uyarıyor.</summary>
    public bool IsEncrypted => Kind != SecretStorageKind.OwnerOnlyFile;

    public static ISecretBackend CreateDefault(string filePath, HostPlatform platform)
    {
        if (platform.IsWindows && OperatingSystem.IsWindows())
        {
            return new DpapiFileBackend(filePath);
        }

        if (platform.IsMac)
        {
            return new MacKeychainBackend();
        }

        return LinuxSecretServiceBackend.IsAvailable()
            ? new LinuxSecretServiceBackend()
            : new OwnerOnlyFileBackend(filePath);
    }

    private static string Key(Guid profileId) => profileId.ToString("N");

    public bool Has(Guid profileId) => _backend.Has(Key(profileId));

    /// <summary>Secret'ı okur; yoksa ya da çözülemiyorsa null döner.</summary>
    public string? Read(Guid profileId) => _backend.Read(Key(profileId));

    /// <summary>Secret'ı yazar. Boş değer kaydı siler.</summary>
    public void Write(Guid profileId, string? secret)
    {
        if (secret is not { Length: > 0 })
        {
            _backend.Delete(Key(profileId));
            return;
        }

        _backend.Write(Key(profileId), secret);
    }

    /// <summary>Profil silindiğinde secret'ı da götürür.</summary>
    public void Remove(Guid profileId) => _backend.Delete(Key(profileId));
}
