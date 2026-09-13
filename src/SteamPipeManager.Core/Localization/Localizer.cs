using System.ComponentModel;
using System.Globalization;

namespace SteamPipeManager.Core.Localization;

/// <summary>
/// Uygulamanın metinlerini sağlar.
///
/// Diller katmanlı çözülür. Kullanıcının dil klasöründeki dosyalar bir <b>üzerine yazma
/// katmanıdır</b>, yerleşik dilin yerine geçmez:
///
///   1. kullanıcının o dildeki dosyası
///   2. uygulamayla gelen aynı dil
///   3. kullanıcının İngilizce dosyası
///   4. uygulamayla gelen İngilizce
///   5. anahtarın kendisi
///
/// Bu sıralama olmadan, uygulama güncellemesiyle eklenen yeni metinler dil dosyası
/// zaten oluşmuş kullanıcılara hiç ulaşmazdı.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public const string FallbackCode = "en";

    /// <summary>
    /// Uygulama genelinde kullanılan örnek. Çekirdek katmandaki kullanıcıya görünen
    /// mesajlar da buradan çevrilir; arayüz açılışta bunu hazırlar.
    /// </summary>
    public static Localizer Current { get; } = new();

    private readonly Dictionary<string, LanguagePack> _userPacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanguagePack> _builtInPacks = new(StringComparer.OrdinalIgnoreCase);

    private string _currentCode = FallbackCode;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Dil dosyalarının okunduğu, kullanıcının düzenleyebildiği klasör.</summary>
    public string LanguageDirectory { get; private set; } = "";

    public string CurrentCode => _currentCode;

    public string CurrentName => Resolve(_currentCode)?.Name ?? _currentCode;

    /// <summary>Kullanıcı dosyaları ve yerleşik diller birlikte listelenir.</summary>
    public IReadOnlyList<LanguagePack> Available =>
    [
        .. _userPacks.Keys
            .Concat(_builtInPacks.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => Resolve(code)!)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
    ];

    /// <summary>XAML bağlamaları bu dizin üzerinden çalışır.</summary>
    public string this[string key] => Get(key);

    public string Get(string key)
    {
        foreach (var pack in ResolutionOrder())
        {
            if (pack.Strings.TryGetValue(key, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        // Anahtarın kendisi görünürse eksik çeviri hemen fark edilir.
        return key;
    }

    /// <summary>Yer tutucuları sırayla doldurur: <c>{0}</c>, <c>{1}</c>…</summary>
    public string Format(string key, params object[] args)
    {
        var template = Get(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // Çeviride yer tutucu bozuksa ham metin gösterilir.
            return template;
        }
    }

    private IEnumerable<LanguagePack> ResolutionOrder()
    {
        if (_userPacks.TryGetValue(_currentCode, out var user))
        {
            yield return user;
        }

        if (_builtInPacks.TryGetValue(_currentCode, out var builtIn))
        {
            yield return builtIn;
        }

        if (!_currentCode.Equals(FallbackCode, StringComparison.OrdinalIgnoreCase))
        {
            if (_userPacks.TryGetValue(FallbackCode, out var userFallback))
            {
                yield return userFallback;
            }

            if (_builtInPacks.TryGetValue(FallbackCode, out var builtInFallback))
            {
                yield return builtInFallback;
            }
        }
    }

    private LanguagePack? Resolve(string code) =>
        _userPacks.TryGetValue(code, out var user) ? user
        : _builtInPacks.TryGetValue(code, out var builtIn) ? builtIn
        : null;

    /// <summary>
    /// <paramref name="builtIn"/> uygulamaya gömülü dil dosyaları (dosya adı → içerik),
    /// <paramref name="userDirectory"/> kullanıcının düzenleyebildiği klasör.
    /// Yerleşik diller klasöre bir kez örnek olarak yazılır; var olan dosyaların
    /// üzerine yazılmaz.
    /// </summary>
    public void Initialize(IReadOnlyDictionary<string, string> builtIn, string userDirectory)
    {
        LanguageDirectory = userDirectory;

        _builtInPacks.Clear();

        foreach (var (fileName, content) in builtIn)
        {
            if (LanguagePack.TryParse(content) is { } pack)
            {
                pack.SourcePath = fileName;
                _builtInPacks[pack.Code] = pack;
            }
        }

        try
        {
            Directory.CreateDirectory(userDirectory);
            WriteMissingBuiltIns(builtIn, userDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Klasör yazılamazsa gömülü sürümlerle devam edilir.
        }

        _userPacks.Clear();

        foreach (var file in EnumerateLanguageFiles(userDirectory))
        {
            if (LanguagePack.TryLoad(file) is { } pack)
            {
                _userPacks[pack.Code] = pack;
            }
        }
    }

    private static IEnumerable<string> EnumerateLanguageFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Order()
            : [];

    /// <summary>
    /// Gömülü dil dosyalarını kullanıcının klasörüne yazar.
    ///
    /// Buradaki denge ince: kullanıcının <b>düzenlediği</b> dosyanın üzerine yazmak
    /// emeğini siler; ama hiç dokunmadığı bir dosyaya da yazmamak, uygulama
    /// güncellemeleriyle gelen metin düzeltmelerinin ona hiç ulaşmaması demek —
    /// yalnızca yeni anahtarlar ulaşır, düzeltilen cümleler ulaşmaz.
    ///
    /// Çözüm: yazdığımız içeriğin parmak izini yanına bırakıyoruz. Dosya hâlâ bizim
    /// yazdığımızla aynıysa kullanıcı ona dokunmamış demektir ve güncellenebilir;
    /// farklıysa düzenlenmiştir ve olduğu gibi bırakılır.
    /// </summary>
    private static void WriteMissingBuiltIns(
        IReadOnlyDictionary<string, string> builtIn,
        string userDirectory)
    {
        foreach (var (fileName, content) in builtIn)
        {
            var target = Path.Combine(userDirectory, fileName);

            if (File.Exists(target) && !IsUntouched(target))
            {
                // Kullanıcı düzenlemiş; emeği korunuyor. Eksik anahtarlar zaten
                // gömülü sürümden tamamlanıyor.
                continue;
            }

            try
            {
                File.WriteAllText(target, content);
                WriteFingerprint(target, content);
            }
            catch (IOException)
            {
                // Yazılamazsa gömülü sürüm yine de kullanılabiliyor.
            }
        }
    }

    /// <summary>Parmak izi dosyası; kullanıcının göreceği bir şey değil.</summary>
    private static string FingerprintPath(string languageFile) => languageFile + ".spm";

    private static string Fingerprint(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content));

        return Convert.ToHexString(bytes);
    }

    private static void WriteFingerprint(string languageFile, string content)
    {
        try
        {
            File.WriteAllText(FingerprintPath(languageFile), Fingerprint(content));
        }
        catch (IOException)
        {
            // Parmak izi yazılamazsa dosya "düzenlenmiş" sayılır; kötü senaryo
            // yalnızca metin düzeltmelerinin gecikmesi.
        }
    }

    /// <summary>
    /// Dosya en son bizim yazdığımız hâlinde mi. Parmak izi yoksa — eski
    /// sürümlerden kalan dosyalar — dokunulmamış sayılmıyor: kullanıcının
    /// düzenlemiş olma ihtimaline karşı temkinli davranılıyor.
    /// </summary>
    private static bool IsUntouched(string languageFile)
    {
        try
        {
            var fingerprint = FingerprintPath(languageFile);

            return File.Exists(fingerprint) &&
                   string.Equals(
                       File.ReadAllText(fingerprint).Trim(),
                       Fingerprint(File.ReadAllText(languageFile)),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Dili değiştirir; bulunamazsa İngilizceye düşer.</summary>
    public bool Use(string? code)
    {
        var target = code is { Length: > 0 } && Resolve(code) is not null
            ? code
            : FallbackCode;

        if (string.Equals(target, _currentCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _currentCode = target;

        // Boş ad tüm bağlamaları tazeler; dizin bağlamaları da buna dahil.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }

    /// <summary>
    /// İlk çalıştırmada sistem diline göre seçer: önce tam eşleşme (<c>de-AT</c>),
    /// sonra ana dil (<c>de</c>), o da yoksa İngilizce.
    /// </summary>
    public string ResolveSystemLanguage(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;

        if (Resolve(culture.Name) is not null)
        {
            return culture.Name;
        }

        var twoLetter = culture.TwoLetterISOLanguageName;

        if (Resolve(twoLetter) is not null)
        {
            return twoLetter;
        }

        var related = Available.FirstOrDefault(
            p => p.Code.StartsWith(twoLetter + "-", StringComparison.OrdinalIgnoreCase));

        return related?.Code ?? FallbackCode;
    }
}
