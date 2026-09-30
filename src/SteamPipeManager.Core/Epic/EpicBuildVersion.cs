using System.Text;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Epic'e gönderilecek sürüm dizesinin doğrulanması ve üretilmesi.
///
/// Steam'de böyle bir şeye gerek yoktu: BuildID'yi Steam döndürür. Epic'te sürümü biz
/// veriyoruz ve <b>tekrarlarsak çakışıyor</b>, dolayısıyla üretmek de doğrulamak da
/// uygulamanın işi.
///
/// Kurallar ölçümden geliyor (BuildPatchTool 1.8.8 üzerinde, tools/BptProbe ile).
///
/// <b>Önemli:</b> iki mod farklı davranıyor. Offline <c>ChunkBuildDirectory</c> gevşek —
/// boşluğu sessizce siliyor, Türkçe karakteri kabul ediyor. Gerçek <c>UploadBinary</c>
/// ise katı:
///
///   <c>BuildVersion string should only contain characters from the following sets
///   a-z, A-Z, 0-9, or .+-_</c>
///
/// Doğrulama <b>katı olana</b> göre yapılıyor: gevşek moda göre yazılmış bir kural,
/// hatayı ancak gerçek yükleme anında ortaya çıkarırdı.
/// </summary>
public static class EpicBuildVersion
{
    public const int MaxLength = 120;

    /// <summary>
    /// <c>UploadBinary</c>'nin kabul ettiği karakterler; aracın kendi hata mesajından
    /// birebir alındı.
    /// </summary>
    public static bool IsAllowed(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '.' or '+' or '-' or '_';

    /// <summary>
    /// Serbest metni (oyun adı gibi) sürümde kullanılabilir hâle getirir: izin
    /// verilmeyen her karakter <c>-</c> oluyor, arka arkaya gelenler tekleniyor.
    ///
    /// Gerekli çünkü varsayılan şablon oyun adını içeriyor ve oyun adlarında boşluk
    /// olması kural, istisna değil.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (IsAllowed(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// Aracın gerçekte kaydedeceği sürüm. Boşluklar silindiği için kullanıcıya
    /// gösterilecek "şu şekilde kaydedilecek" değeri budur.
    /// </summary>
    public static string Normalize(string version) =>
        version is null ? "" : new string([.. version.Where(c => !char.IsWhiteSpace(c))]);

    public static ValidationResult Validate(string? version)
    {
        var issues = new List<ValidationIssue>();
        var value = version ?? "";

        if (Normalize(value).Length == 0)
        {
            // Araç boş sürümü kabul edip isimsiz bir build üretiyor; kullanıcı bunu
            // Epic tarafında tanıyamaz.
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Version.Required")));

            return new ValidationResult(issues);
        }

        // Katı kural UploadBinary'den; offline mod daha gevşek ama gerçek yükleme
        // budur, dolayısıyla doğrulama buna göre.
        if (value.Any(c => !IsAllowed(c)))
        {
            var used = value.Where(c => !IsAllowed(c)).Distinct()
                .Select(c => char.IsWhiteSpace(c) ? "␣" : c.ToString());

            issues.Add(new(
                IssueSeverity.Error,
                Loc.T("Epic.Version.ForbiddenCharacters", string.Join(" ", used))));
        }

        if (Normalize(value).Length > MaxLength)
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Version.TooLong", MaxLength)));
        }


        return new ValidationResult(issues);
    }

    /// <summary>
    /// Şablondaki yer tutucuları doldurur. <c>{n}</c> ayrı ele alınıyor çünkü değeri
    /// mevcut sürümlere bakılarak belirleniyor.
    /// </summary>
    public static string Render(
        string template,
        string appTitle,
        string targetTitle,
        SubAppKind kind,
        string artifactId,
        int counter,
        DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.Now;

        // Sonucun tamamı temizleniyor, yalnızca yer tutucuların değeri değil: şablonun
        // kendisinde de boşluk olabiliyor ("{app} {kind}") ve süslü parantezler zaten
        // izin verilen kümede değil. Böylece hangi şablon yazılırsa yazılsın üretilen
        // sürüm UploadBinary'nin kabul ettiği biçimde çıkıyor.
        var rendered = (template ?? "")
            .Replace("{app}", appTitle)
            .Replace("{target}", targetTitle)
            .Replace("{kind}", kind.ToString())
            .Replace("{artifact}", artifactId)
            .Replace("{date}", moment.ToString("yyyy-MM-dd"))
            .Replace("{time}", moment.ToString("HHmm"))
            .Replace("{n}", counter.ToString());

        return Sanitize(rendered);
    }

    /// <summary>
    /// Şablondan, daha önce kullanılmamış bir sürüm üretir.
    ///
    /// Karşılaştırma <see cref="Normalize"/> üzerinden yapılıyor: aracın gözünde
    /// "1.0 beta" ile "1.0beta" aynı sürüm, dolayısıyla bizim gözümüzde de öyle olmalı.
    /// Şablonda <c>{n}</c> yoksa ve üretilen sürüm zaten kullanılmışsa sona <c>-2</c>,
    /// <c>-3</c> … eklenir; sessizce çakışan bir sürüm döndürmek en kötüsü olurdu.
    /// </summary>
    public static string NextUnique(
        string template,
        IEnumerable<string> existingVersions,
        string appTitle,
        string targetTitle,
        SubAppKind kind,
        string artifactId,
        DateTimeOffset? now = null)
    {
        var taken = new HashSet<string>(
            (existingVersions ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase);

        var usesCounter = (template ?? "").Contains("{n}", StringComparison.Ordinal);

        for (var attempt = 1; attempt <= 10_000; attempt++)
        {
            var candidate = Render(template, appTitle, targetTitle, kind, artifactId, attempt, now);

            if (!usesCounter && attempt > 1)
            {
                candidate = $"{candidate}-{attempt}";
            }

            if (!taken.Contains(Normalize(candidate)))
            {
                return candidate;
            }
        }

        // Buraya düşmek için on bin sürümün dolu olması gerekir; yine de sessizce
        // çakışmaktansa zaman damgasına düşülüyor.
        return Render(template, appTitle, targetTitle, kind, artifactId, 1, now) +
               $"-{(now ?? DateTimeOffset.Now):yyyyMMddHHmmss}";
    }
}
