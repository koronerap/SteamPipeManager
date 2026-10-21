using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Net.Codecrete.QrCodeGenerator;

namespace SteamPipeManager.Presentation.Services;

/// <summary>
/// Masaüstünden Android'deki Pipe Manager Pulse'a aktarım: Steam oyunları ve finans
/// anahtarları bir QR'da. QR anahtarları içerdiği için şifreli; ekranda ayrıca gösterilen
/// 8 haneli kod olmadan açılmıyor (QR'ın fotoğrafı tek başına işe yaramıyor).
///
/// Biçim (Android'deki Pairing.kt ile aynı): <c>SPMPULSE1.</c> + base64url(tuz[16] |
/// nonce[12] | şifreli + etiket[16]). Anahtar PBKDF2-HMAC-SHA256(kod, tuz, 600.000 tur,
/// 32 bayt); AES-256-GCM, ek veri "SPMPULSE1"; içerik ham DEFLATE ile sıkıştırılmış JSON.
/// </summary>
public static class PhonePairing
{
    public const string Prefix = "SPMPULSE1.";
    public const int Iterations = 600_000;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Aad = "SPMPULSE1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Türkçe ve Japonca adlar kaçış dizisine dönmesin: QR küçük kalsın.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="Key">Anahtar listesindeki sırası; anahtarı yoksa null (yalnızca oyuncu sayısı).</param>
    public sealed record Game(uint Id, string Title, int? Key);

    /// <param name="Currency">Masaüstünde seçilen para birimi; otomatikse null (telefon kendi bölgesini kullanıyor).</param>
    public sealed record Payload(int V, IReadOnlyList<Game> Games, IReadOnlyList<string> Keys, string? Currency)
    {
        public bool Matches(Payload other) =>
            V == other.V && Currency == other.Currency && Games.SequenceEqual(other.Games) && Keys.SequenceEqual(other.Keys);
    }

    /// <summary>
    /// Steam oyunlarından aktarılacak veri. Her oyun ilk anahtarlı profilinin anahtarıyla;
    /// aynı anahtar bir kez gönderiliyor (aynı şirketin profilleri).
    /// </summary>
    public static Payload Build(IReadOnlyList<AnalyticsTarget> targets, Func<Guid, string?> keyOf, string? currency)
    {
        var plan = AnalyticsPlan.Build(targets, keyOf);
        var accounts = plan.Scope.Accounts.ToList();

        var games = targets
            .Select(t => new Game(
                t.AppId,
                t.Title,
                plan.AccountOfApp.TryGetValue(t.AppId, out var account) ? accounts.FindIndex(a => a.Id == account) : null))
            .ToList();

        return new Payload(1, games, [.. accounts.Select(a => a.Key)], currency);
    }

    /// <summary>Ekranda gösterilen 8 haneli kod; kriptografik rastgele.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");

    /// <summary>"12345678" → "1234 5678": okuması kolay.</summary>
    public static string FormatCode(string code) => code.Length == 8 ? code[..4] + " " + code[4..] : code;

    public static string Seal(Payload payload, string code, byte[]? salt = null, byte[]? nonce = null, int iterations = Iterations)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        nonce ??= RandomNumberGenerator.GetBytes(NonceSize);

        var plain = Deflate(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var key = DeriveKey(code, salt, iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Aad);
        }

        return Prefix + Base64Url([.. salt, .. nonce, .. cipher, .. tag]);
    }

    /// <summary>Kod yanlışsa ya da veri bozuksa null.</summary>
    public static Payload? Open(string text, string code, int iterations = Iterations)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = FromBase64Url(text[Prefix.Length..]);

            if (bytes.Length <= SaltSize + NonceSize + TagSize)
            {
                return null;
            }

            var salt = bytes[..SaltSize];
            var nonce = bytes[SaltSize..(SaltSize + NonceSize)];
            var cipher = bytes[(SaltSize + NonceSize)..^TagSize];
            var tag = bytes[^TagSize..];
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(DeriveKey(code, salt, iterations), TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain, Aad);
            }

            return JsonSerializer.Deserialize<Payload>(Inflate(plain), Json);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>QR'ın kareleri (koyu = true); kenar boşluğunu çizici ekliyor.</summary>
    public static bool[,] Modules(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var modules = new bool[qr.Size, qr.Size];

        for (var y = 0; y < qr.Size; y++)
        {
            for (var x = 0; x < qr.Size; x++)
            {
                modules[y, x] = qr.GetModule(x, y);
            }
        }

        return modules;
    }

    private static byte[] DeriveKey(string code, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(new string(code.Where(char.IsAsciiDigit).ToArray())), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();

        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
