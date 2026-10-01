using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Epic;

/// <summary>Secret'ın nerede ve nasıl korunduğu; arayüz kullanıcıya bunu söylüyor.</summary>
public enum SecretStorageKind
{
    /// <summary>Windows DPAPI ile şifreli dosya.</summary>
    WindowsDpapi,

    /// <summary>macOS Anahtar Zinciri (Keychain).</summary>
    MacKeychain,

    /// <summary>Linux masaüstünün gizli bilgi servisi (GNOME Keyring, KWallet) — <c>secret-tool</c> ile.</summary>
    LinuxSecretService,

    /// <summary>
    /// Şifresiz, yalnızca sahibinin okuyabildiği (0600) dosya. Gizli bilgi servisi
    /// olmayan Linux sistemlerinde son çare; arayüz bunu açıkça belirtiyor.
    /// </summary>
    OwnerOnlyFile,
}

/// <summary>Bir secret deposu. Anahtar profil kimliği.</summary>
public interface ISecretBackend
{
    SecretStorageKind Kind { get; }

    bool Has(string key);

    string? Read(string key);

    void Write(string key, string secret);

    void Delete(string key);
}

/// <summary>Dış bir komutu çalıştırır; testler sahtesini veriyor.</summary>
public interface ICommandRunner
{
    /// <param name="standardInput">Komuta stdin'den verilecek metin; secret'lar argümanda değil burada.</param>
    CommandResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null);
}

public sealed record CommandResult(int ExitCode, string Output);

public sealed class ProcessCommandRunner : ICommandRunner
{
    public static readonly ProcessCommandRunner Instance = new();

    public CommandResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
            }

            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return new CommandResult(-1, "");
            }

            return new CommandResult(process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Program yok.
            return new CommandResult(-1, "");
        }
    }
}

/// <summary>
/// Windows: DPAPI ile <see cref="DataProtectionScope.CurrentUser"/> kapsamında şifreli
/// JSON dosyası. Dosya başka bir kullanıcıya ya da makineye kopyalansa çözülemiyor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileBackend(string filePath) : ISecretBackend
{
    /// <summary>
    /// DPAPI'ye verilen ek giriş. Aynı kullanıcının başka bir programının bu dosyayı
    /// çözmesini zorlaştırır.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SteamPipeManager.Epic.ClientSecret.v1");

    private readonly JsonSecretFile _file = new(filePath);

    public SecretStorageKind Kind => SecretStorageKind.WindowsDpapi;

    public bool Has(string key) => _file.Load().ContainsKey(key);

    public string? Read(string key)
    {
        if (!_file.Load().TryGetValue(key, out var encrypted))
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

    public void Write(string key, string secret)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);

        var entries = _file.Load();
        entries[key] = Convert.ToBase64String(encrypted);
        _file.Save(entries);
    }

    public void Delete(string key)
    {
        var entries = _file.Load();

        if (entries.Remove(key))
        {
            _file.Save(entries);
        }
    }
}

/// <summary>
/// macOS Anahtar Zinciri, Apple'ın <c>security</c> aracıyla.
///
/// Not: <c>add-generic-password</c> secret'ı argüman olarak alıyor; araç birkaç
/// milisaniye çalıştığı için bu sürede aynı kullanıcının süreç listesinde görünebilir.
/// BuildPatchTool'a ise hâlâ yalnızca ortam değişkeniyle veriliyor.
/// </summary>
public sealed class MacKeychainBackend(ICommandRunner? runner = null) : ISecretBackend
{
    public const string Service = "SteamPipeManager.Epic.ClientSecret";

    private readonly ICommandRunner _runner = runner ?? ProcessCommandRunner.Instance;

    public SecretStorageKind Kind => SecretStorageKind.MacKeychain;

    public bool Has(string key) =>
        _runner.Run("security", ["find-generic-password", "-s", Service, "-a", key]).ExitCode == 0;

    public string? Read(string key)
    {
        var result = _runner.Run("security", ["find-generic-password", "-s", Service, "-a", key, "-w"]);

        return result.ExitCode == 0 && result.Output.TrimEnd('\n', '\r') is { Length: > 0 } secret
            ? secret
            : null;
    }

    public void Write(string key, string secret)
    {
        // -U: varsa güncelle.
        var result = _runner.Run(
            "security", ["add-generic-password", "-U", "-s", Service, "-a", key, "-l", "Steam Pipe Manager — Epic", "-w", secret]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The secret could not be saved to the Keychain (security exited with {result.ExitCode}).");
        }
    }

    public void Delete(string key) =>
        _runner.Run("security", ["delete-generic-password", "-s", Service, "-a", key]);
}

/// <summary>
/// Linux masaüstünün gizli bilgi servisi (Secret Service: GNOME Keyring, KWallet),
/// <c>secret-tool</c> ile. Secret araca stdin'den veriliyor, argümanda görünmüyor.
/// </summary>
public sealed class LinuxSecretServiceBackend(ICommandRunner? runner = null) : ISecretBackend
{
    public const string Application = "SteamPipeManager";

    private readonly ICommandRunner _runner = runner ?? ProcessCommandRunner.Instance;

    public SecretStorageKind Kind => SecretStorageKind.LinuxSecretService;

    private static string[] Attributes(string key) => ["application", Application, "profile", key];

    public bool Has(string key) => Read(key) is not null;

    public string? Read(string key)
    {
        var result = _runner.Run("secret-tool", ["lookup", .. Attributes(key)]);

        return result.ExitCode == 0 && result.Output.TrimEnd('\n', '\r') is { Length: > 0 } secret
            ? secret
            : null;
    }

    public void Write(string key, string secret)
    {
        var result = _runner.Run(
            "secret-tool", ["store", "--label=Steam Pipe Manager — Epic", .. Attributes(key)], secret);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The secret could not be saved to the Secret Service (secret-tool exited with {result.ExitCode}).");
        }
    }

    public void Delete(string key) => _runner.Run("secret-tool", ["clear", .. Attributes(key)]);

    /// <summary>
    /// Servis kullanılabilir mi: araç var ve bir masaüstü oturumu yanıt veriyor.
    /// Başsız sunucularda ya da servis kurulu değilse false.
    /// </summary>
    public static bool IsAvailable(ICommandRunner? runner = null)
    {
        if (TerminalLauncher.FindOnPath("secret-tool") is null)
        {
            return false;
        }

        // Yalnızca aracın varlığı yetmiyor: arkasında bir servis (GNOME Keyring, KWallet)
        // çalışmıyorsa secret-tool her işlemde hata veriyor, ve "bulunamadı" ile "servis
        // yok" aynı çıkış koduyla dönüyor. Bu yüzden bir deneme kaydı yazılıp geri
        // okunuyor; ikisi de tutarsa servis gerçekten kullanılabilir.
        runner ??= ProcessCommandRunner.Instance;
        var probe = Guid.NewGuid().ToString("N");
        string[] attributes = ["application", Application, "probe", probe];

        var stored = runner.Run("secret-tool", ["store", "--label=Steam Pipe Manager probe", .. attributes], probe);

        try
        {
            return stored.ExitCode == 0 &&
                   runner.Run("secret-tool", ["lookup", .. attributes]) is { ExitCode: 0 } found &&
                   found.Output.Trim() == probe;
        }
        finally
        {
            runner.Run("secret-tool", ["clear", .. attributes]);
        }
    }
}

/// <summary>
/// Şifresiz ama yalnızca sahibinin okuyabildiği (0600) JSON dosyası. Gizli bilgi
/// servisi olmayan Linux sistemleri için; SSH anahtarlarının korunma biçimiyle aynı.
/// </summary>
public sealed class OwnerOnlyFileBackend(string filePath) : ISecretBackend
{
    private readonly JsonSecretFile _file = new(filePath, ownerOnly: true);

    public SecretStorageKind Kind => SecretStorageKind.OwnerOnlyFile;

    public bool Has(string key) => _file.Load().ContainsKey(key);

    public string? Read(string key) => _file.Load().TryGetValue(key, out var secret) ? secret : null;

    public void Write(string key, string secret)
    {
        var entries = _file.Load();
        entries[key] = secret;
        _file.Save(entries);
    }

    public void Delete(string key)
    {
        var entries = _file.Load();

        if (entries.Remove(key))
        {
            _file.Save(entries);
        }
    }
}

/// <summary>Anahtar → değer JSON dosyası; geçici dosya üzerinden yazılıyor.</summary>
internal sealed class JsonSecretFile(string filePath, bool ownerOnly = false)
{
    public Dictionary<string, string> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Bozuk depo, secret'ın kaybolması demek — kullanıcı yeniden girer.
            // Build'i engellememesi için sessizce boş kabul ediliyor.
            return [];
        }
    }

    public void Save(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var temporary = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            // İzin, içerik yazılmadan önce kısıtlanıyor: dosya bir an bile herkese
            // okunabilir durmasın.
            File.WriteAllText(temporary, "");

            if (ownerOnly)
            {
                UnixPermissions.RestrictToOwner(temporary);
            }

            File.WriteAllText(temporary, json);
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
