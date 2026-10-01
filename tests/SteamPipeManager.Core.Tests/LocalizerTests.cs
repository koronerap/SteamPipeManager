using System.Globalization;
using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.Tests;

public sealed class LocalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_lang_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Dictionary<string, string> BuiltIn(params (string File, string Code, string Name, (string, string)[] Strings)[] packs)
    {
        var result = new Dictionary<string, string>();

        foreach (var (file, code, name, strings) in packs)
        {
            var body = string.Join(",\n", strings.Select(s => $"    \"{s.Item1}\": \"{s.Item2}\""));
            result[file] = $"{{\n  \"code\": \"{code}\",\n  \"name\": \"{name}\",\n  \"strings\": {{\n{body}\n  }}\n}}";
        }

        return result;
    }

    private static Dictionary<string, string> StandardBuiltIn() => BuiltIn(
        ("en.json", "en", "English", [("Greeting", "Hello"), ("Only.En", "English only")]),
        ("tr.json", "tr", "Türkçe", [("Greeting", "Merhaba")]));

    private Localizer Create(Dictionary<string, string>? builtIn = null)
    {
        var localizer = new Localizer();
        localizer.Initialize(builtIn ?? StandardBuiltIn(), _dir);
        return localizer;
    }

    [Fact]
    public void Writes_built_in_files_to_the_user_folder()
    {
        Create();

        Assert.True(File.Exists(Path.Combine(_dir, "en.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "tr.json")));
    }

    [Fact]
    public void Defaults_to_english()
    {
        var localizer = Create();

        Assert.Equal("en", localizer.CurrentCode);
        Assert.Equal("Hello", localizer.Get("Greeting"));
    }

    [Fact]
    public void Switches_language()
    {
        var localizer = Create();

        Assert.True(localizer.Use("tr"));
        Assert.Equal("Merhaba", localizer.Get("Greeting"));
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        var localizer = Create();
        localizer.Use("zz");

        Assert.Equal("en", localizer.CurrentCode);
    }

    [Fact]
    public void Missing_key_in_current_language_falls_back_to_english()
    {
        var localizer = Create();
        localizer.Use("tr");

        // tr paketinde bu anahtar yok; İngilizcesi gösterilmeli.
        Assert.Equal("English only", localizer.Get("Only.En"));
    }

    [Fact]
    public void Unknown_key_returns_the_key_itself()
    {
        // Eksik çeviri boş metin yerine anahtar olarak görünsün ki hemen fark edilsin.
        Assert.Equal("No.Such.Key", Create().Get("No.Such.Key"));
    }

    /// <summary>
    /// Asıl tuzak buydu: kullanıcının dil dosyası varken uygulama güncellemesiyle
    /// eklenen yeni anahtarlar ona hiç ulaşmıyordu. Kullanıcı dosyası artık
    /// yerleşiğin yerine geçmiyor, üzerine yazıyor.
    /// </summary>
    [Fact]
    public void New_built_in_keys_reach_users_who_already_have_a_language_file()
    {
        // Kullanıcıda eski sürüm dosya var: yalnızca Greeting içeriyor.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "tr.json"),
            "{\"code\":\"tr\",\"name\":\"Türkçe\",\"strings\":{\"Greeting\":\"Selam\"}}");

        var builtIn = BuiltIn(
            ("en.json", "en", "English", [("Greeting", "Hello"), ("New.Key", "Brand new")]),
            ("tr.json", "tr", "Türkçe", [("Greeting", "Merhaba"), ("New.Key", "Yepyeni")]));

        var localizer = Create(builtIn);
        localizer.Use("tr");

        // Kullanıcının çevirisi kazanır…
        Assert.Equal("Selam", localizer.Get("Greeting"));

        // …ama yeni anahtar yerleşik sürümden gelir.
        Assert.Equal("Yepyeni", localizer.Get("New.Key"));
    }

    [Fact]
    public void User_file_is_not_overwritten()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "tr.json");
        File.WriteAllText(path, "{\"code\":\"tr\",\"name\":\"Türkçe\",\"strings\":{\"Greeting\":\"Selam\"}}");

        Create();

        Assert.Contains("Selam", File.ReadAllText(path));
    }

    [Fact]
    public void User_supplied_language_appears_in_the_list()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "de.json"),
            "{\"code\":\"de\",\"name\":\"Deutsch\",\"strings\":{\"Greeting\":\"Hallo\"}}");

        var localizer = Create();

        Assert.Contains(localizer.Available, p => p.Code == "de" && p.Name == "Deutsch");

        localizer.Use("de");
        Assert.Equal("Hallo", localizer.Get("Greeting"));
    }

    [Fact]
    public void Broken_language_file_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");

        var localizer = Create();

        // Bozuk dosya uygulamayı engellememeli; diğer diller çalışmaya devam eder.
        Assert.Equal("Hello", localizer.Get("Greeting"));
        Assert.Contains(localizer.Available, p => p.Code == "en");
    }

    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("tr", "tr")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    public void Resolves_system_language(string culture, string expected)
    {
        Assert.Equal(expected, Create().ResolveSystemLanguage(new CultureInfo(culture)));
    }

    [Fact]
    public void Format_fills_placeholders()
    {
        var builtIn = BuiltIn(("en.json", "en", "English", [("Count", "{0} of {1}")]));

        Assert.Equal("2 of 5", Create(builtIn).Format("Count", 2, 5));
    }

    [Fact]
    public void Format_survives_a_broken_placeholder()
    {
        var builtIn = BuiltIn(("en.json", "en", "English", [("Bad", "{0} and {oops}")]));

        // Bozuk çeviri istisna fırlatmamalı; ham metin gösterilir.
        Assert.Equal("{0} and {oops}", Create(builtIn).Format("Bad", 1));
    }

    [Fact]
    public void Language_change_raises_a_refresh_notification()
    {
        var localizer = Create();
        var names = new List<string?>();

        localizer.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        localizer.Use("tr");

        // Boş ad WPF'te tüm bağlamaları tazeliyor; Avalonia dizin bağlamaları ise
        // "Item[]" bekliyor. Canlı dil değişimi ikisine de dayanıyor.
        Assert.Contains(names, n => string.IsNullOrEmpty(n));
        Assert.Contains("Item[]", names);
    }
}
