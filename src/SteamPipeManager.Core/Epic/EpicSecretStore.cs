using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Epic client secret'larının yerel, şifreli deposu.
///
/// Neden ayrı bir dosya: <c>profiles.json</c>'ın yedeklenebilir ve paylaşılabilir kalması
/// Steam tarafında verdiğimiz bir sözdü ve README'de yazıyor. Epic'te secret'ın kalıcı
/// olması gerekiyor (Steam'deki gibi aracın hatırladığı bir oturum yok), ama bu sözü
/// bozmanın gerekçesi olamaz — secret buraya, profil dosyasının dışına yazılıyor.
///
/// Şifreleme <b>DPAPI</b> ile ve <see cref="DataProtectionScope.CurrentUser"/> kapsamında:
/// dosya başka bir kullanıcıya ya da başka bir makineye kopyalansa çözülemiyor. Bu, bir
/// parola yöneticisi değil; amacı, düz metin secret'ın diskte durmasını engellemek.
/// </summary>
public sealed class EpicSecretStore(string filePath)
{
    /// <summary>
    /// DPAPI'ye verilen ek giriş. Aynı kullanıcının başka bir programının bu dosyayı
    /// çözmesini zorlaştırır.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SteamPipeManager.Epic.ClientSecret.v1");

    public string FilePath { get; } = filePath;

    /// <summary>Profil kimliği → şifrelenmiş secret (base64).</summary>
    private Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                   ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Bozuk depo, secret'ın kaybolması demek — kullanıcı yeniden girer.
            // Build'i engellememesi için sessizce boş kabul ediliyor.
            return [];
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var temporary = FilePath + ".tmp";

        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, overwrite: true);
    }

    public bool Has(Guid profileId) => Load().ContainsKey(profileId.ToString("N"));

    /// <summary>Secret'ı çözer; yoksa ya da çözülemiyorsa null döner.</summary>
    public string? Read(Guid profileId)
    {
        if (!Load().TryGetValue(profileId.ToString("N"), out var encrypted))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Başka bir kullanıcı/makine tarafından yazılmış olabilir; çözülemiyorsa
            // yok sayılır ve kullanıcıdan yeniden istenir.
            return null;
        }
    }

    /// <summary>Secret'ı şifreleyip yazar. Boş değer kaydı siler.</summary>
    public void Write(Guid profileId, string? secret)
    {
        var entries = Load();
        var key = profileId.ToString("N");

        if (secret is not { Length: > 0 })
        {
            entries.Remove(key);
            Save(entries);

            return;
        }

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);

        entries[key] = Convert.ToBase64String(encrypted);
        Save(entries);
    }

    /// <summary>Profil silindiğinde secret'ı da götürür.</summary>
    public void Remove(Guid profileId) => Write(profileId, null);
}
