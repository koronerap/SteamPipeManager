using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Dil dosyalarının güncellenmesi.
///
/// İki ihtiyaç çatışıyor: kullanıcının düzenlediği dosya korunmalı, ama hiç
/// dokunmadığı dosyaya uygulama güncellemeleriyle gelen metin düzeltmeleri de
/// ulaşmalı. Aksi hâlde yalnızca <b>yeni</b> anahtarlar ulaşır, düzeltilen cümleler
/// kullanıcıda sonsuza kadar eski kalır.
/// </summary>
public sealed class LanguageRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_langref_{Guid.NewGuid():N}");

    public LanguageRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Pack(string greeting) =>
        $$"""{ "code": "en", "name": "English", "strings": { "Hello": "{{greeting}}" } }""";

    private string LanguageFile => Path.Combine(_dir, "en.json");

    private Localizer Load(string builtIn)
    {
        var localizer = new Localizer();
        localizer.Initialize(new Dictionary<string, string> { ["en.json"] = builtIn }, _dir);
        localizer.Use("en");

        return localizer;
    }

    [Fact]
    public void The_built_in_pack_is_written_on_first_run()
    {
        Load(Pack("Hello"));

        Assert.True(File.Exists(LanguageFile));
        Assert.Contains("Hello", File.ReadAllText(LanguageFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// Asıl düzeltme: kullanıcı dosyaya dokunmadıysa yeni metin ona ulaşmalı.
    /// </summary>
    [Fact]
    public void An_untouched_file_receives_corrected_text()
    {
        Load(Pack("Hello"));

        var updated = Load(Pack("Hello there"));

        Assert.Equal("Hello there", updated.Get("Hello"));
        Assert.Contains("Hello there", File.ReadAllText(LanguageFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kullanıcı çevirisini düzenlediyse emeği silinmemeli — bu, düzeltmelerin
    /// ulaşmasından daha önemli.
    /// </summary>
    [Fact]
    public void An_edited_file_is_left_alone()
    {
        Load(Pack("Hello"));

        File.WriteAllText(LanguageFile, Pack("Merhaba"));

        var updated = Load(Pack("Hello there"));

        Assert.Equal("Merhaba", updated.Get("Hello"));
    }

    /// <summary>
    /// Eski sürümlerden kalan, parmak izi olmayan dosyalar düzenlenmiş sayılıyor:
    /// emin olmadığımızda kullanıcının emeğini korumak doğru taraf.
    /// </summary>
    [Fact]
    public void A_file_without_a_fingerprint_is_treated_as_edited()
    {
        File.WriteAllText(LanguageFile, Pack("Eski"));

        var updated = Load(Pack("Yeni"));

        Assert.Equal("Eski", updated.Get("Hello"));
    }

    /// <summary>Parmak izi dosyası dil listesine karışmamalı.</summary>
    [Fact]
    public void The_fingerprint_file_is_not_mistaken_for_a_language()
    {
        var localizer = Load(Pack("Hello"));

        Assert.Single(localizer.Available);
        Assert.Equal("en", localizer.Available[0].Code);
    }
}
