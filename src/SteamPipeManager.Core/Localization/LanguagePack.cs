using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamPipeManager.Core.Localization;

/// <summary>
/// Bir dil dosyası. Kullanıcılar kendi dosyalarını dil klasörüne bırakarak yeni dil
/// ekleyebilir; uygulama yeniden derlenmez.
/// </summary>
public sealed class LanguagePack
{
    /// <summary>Dosya adından bağımsız dil kodu (ör. <c>en</c>, <c>tr</c>, <c>de-AT</c>).</summary>
    public string Code { get; set; } = "";

    /// <summary>Dil listesinde gösterilecek ad (ör. "Türkçe").</summary>
    public string Name { get; set; } = "";

    public Dictionary<string, string> Strings { get; set; } = [];

    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LanguagePack? TryLoad(string path)
    {
        try
        {
            var pack = TryParse(File.ReadAllText(path));

            if (pack is null)
            {
                return null;
            }

            pack.SourcePath = path;
            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Okunamayan dil dosyası uygulamayı engellememeli.
            return null;
        }
    }

    /// <summary>Gömülü kaynaklardan okumak için: dosya yolu olmadan ayrıştırır.</summary>
    public static LanguagePack? TryParse(string json)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<LanguagePack>(json, JsonOptions);

            if (pack is null || pack.Code.Length == 0)
            {
                return null;
            }

            if (pack.Name.Length == 0)
            {
                pack.Name = pack.Code;
            }

            return pack;
        }
        catch (JsonException)
        {
            // Bozuk dil dosyası yok sayılır.
            return null;
        }
    }
}
