using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Uygulamayla gelen dil dosyalarını denetler. Eksik bir anahtar İngilizceye düşerek
/// sessizce gizlenir, bozuk bir yer tutucu ise çalışma anında metni ham hâlde bırakır —
/// ikisi de ancak o ekran açıldığında fark edilirdi.
/// </summary>
public sealed class LanguageFileTests
{
    private static readonly string LangDirectory = FindLangDirectory();

    /// <summary>Dosyalar kaynak ağacından okunur; çıktıya kopyalamak gerekmiyor.</summary>
    private static string FindLangDirectory([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "SteamPipeManager.Presentation", "lang");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("lang klasörü bulunamadı.");
    }

    private static LanguagePack Load(string code) =>
        LanguagePack.TryLoad(Path.Combine(LangDirectory, code + ".json"))
        ?? throw new InvalidOperationException($"{code}.json okunamadı.");

    private static LanguagePack English => Load("en");

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();

        foreach (var file in Directory.EnumerateFiles(LangDirectory, "*.json").Order())
        {
            var code = Path.GetFileNameWithoutExtension(file);

            if (!code.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                data.Add(code);
            }
        }

        return data;
    }

    [Fact]
    public void The_expected_languages_ship_with_the_app()
    {
        var codes = Directory.EnumerateFiles(LangDirectory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order()
            .ToArray();

        Assert.Equal(
            ["de", "en", "fr", "ja", "ko", "ru", "tr", "zh-Hans"],
            codes);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_declares_its_code_and_a_name(string code)
    {
        var pack = Load(code);

        Assert.Equal(code, pack.Code, ignoreCase: true);
        Assert.NotEmpty(pack.Name);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_covers_every_key(string code)
    {
        var missing = English.Strings.Keys
            .Where(k => !Load(code).Strings.ContainsKey(k))
            .Order()
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void No_language_carries_keys_the_app_never_asks_for(string code)
    {
        var stale = Load(code).Strings.Keys
            .Where(k => !English.Strings.ContainsKey(k))
            .Order()
            .ToArray();

        Assert.Empty(stale);
    }

    /// <summary>
    /// Çeviride yer tutucu eksikse o değer hiç görünmez (ör. hata sebebi kaybolur);
    /// fazlaysa <c>string.Format</c> patlar ve metin ham şablon olarak kalır.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_match_the_English_original(string code)
    {
        var pack = Load(code);
        var mismatched = new List<string>();

        foreach (var (key, english) in English.Strings)
        {
            if (!pack.Strings.TryGetValue(key, out var translated))
            {
                continue;
            }

            if (!Placeholders(english).SetEquals(Placeholders(translated)))
            {
                mismatched.Add(key);
            }
        }

        Assert.Empty(mismatched);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Nothing_is_left_untranslated_as_an_empty_string(string code)
    {
        var blank = Load(code).Strings
            .Where(pair => pair.Value.Trim().Length == 0)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();

        Assert.Empty(blank);
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{\d+\}").Select(m => m.Value)];
}
