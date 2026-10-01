namespace SteamPipeManager.Presentation.Localization;

/// <summary>
/// Uygulamayla gelen dil dosyaları. Paylaşılan projeye gömülü; WPF ve Avalonia
/// arayüzleri aynı metinleri buradan alıyor.
/// </summary>
public static class EmbeddedLanguages
{
    /// <summary>Dosya adı (<c>en.json</c>) → içerik.</summary>
    public static Dictionary<string, string> Read()
    {
        var assembly = typeof(EmbeddedLanguages).Assembly;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains(".lang.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);

            // Kaynak adı "SteamPipeManager.Presentation.lang.en.json" biçiminde; dosya adına indiriliyor.
            var fileName = name[(name.IndexOf(".lang.", StringComparison.OrdinalIgnoreCase) + 6)..];
            result[fileName] = reader.ReadToEnd();
        }

        return result;
    }
}
