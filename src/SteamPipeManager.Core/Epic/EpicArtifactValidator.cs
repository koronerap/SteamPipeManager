using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Bir Epic hedefinin build almaya hazır olup olmadığını denetler.
///
/// Steam'deki <see cref="ProfileValidator"/>'ın karşılığı, ama kurallar farklı — ve bir
/// kısmı doğrudan ölçümden geliyor (docs/E0-BPT-FINDINGS.md):
///
///   * boş build kökünü araç zaten reddediyor (Bulgu 13), biz aynı şeyi <b>önceden</b>
///     söylüyoruz ki kullanıcı süreç başlatıp beklemesin
///   * sürüm dizesinin kuralları Bulgu 16'dan
///
/// Diske dokunan kontroller <paramref name="checkFileSystem"/> ile ayrılabiliyor;
/// testler böylece var olmayan yollarla çalışabiliyor.
/// </summary>
public static class EpicArtifactValidator
{
    public static ValidationResult Validate(
        EpicArtifact artifact,
        EpicProfileSettings? profile = null,
        EpicGameSettings? game = null,
        bool checkFileSystem = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var issues = new List<ValidationIssue>();

        ValidateIdentity(artifact, profile, game, issues);
        ValidateContent(artifact, checkFileSystem, issues);
        ValidateGoLive(artifact, issues);

        // Şablonun kendisi değil, ürettiği sürüm doğrulanıyor: şablon süslü parantez ve
        // boşluk içerebilir, bunlar doldurulurken temizleniyor. Asıl soru, sonuçta
        // gönderilebilir bir sürüm çıkıp çıkmadığı — örneğin tamamı yer tutucudan
        // oluşan bir şablon boş sürüm üretebilir.
        var sample = EpicBuildVersion.Render(
            artifact.BuildVersionTemplate, "Game", artifact.Title, artifact.Kind,
            artifact.ArtifactId, 1);

        foreach (var issue in EpicBuildVersion.Validate(sample).Issues)
        {
            if (issue.Severity == IssueSeverity.Error)
            {
                issues.Add(issue with { Field = nameof(artifact.BuildVersionTemplate) });
            }
        }

        return new ValidationResult(issues);
    }

    private static void ValidateIdentity(
        EpicArtifact artifact,
        EpicProfileSettings? profile,
        EpicGameSettings? game,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(artifact.Title))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Validate.TargetNameRequired"),
                nameof(artifact.Title)));
        }

        if (string.IsNullOrWhiteSpace(artifact.ArtifactId))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.ArtifactIdRequired"),
                nameof(artifact.ArtifactId)));
        }

        if (profile is not null && !profile.IsComplete)
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.ProfileIncomplete")));
        }

        if (game is not null && string.IsNullOrWhiteSpace(game.ProductId))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.ProductIdRequired")));
        }
    }

    private static void ValidateContent(
        EpicArtifact artifact,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(artifact.BuildRoot))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.BuildRootRequired"),
                nameof(artifact.BuildRoot)));

            return;
        }

        if (string.IsNullOrWhiteSpace(artifact.AppLaunch))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.AppLaunchRequired"),
                nameof(artifact.AppLaunch)));
        }

        if (!checkFileSystem)
        {
            return;
        }

        if (!Directory.Exists(artifact.BuildRoot))
        {
            issues.Add(new(IssueSeverity.Error,
                Loc.T("Epic.Validate.BuildRootMissing", artifact.BuildRoot),
                nameof(artifact.BuildRoot)));

            return;
        }

        // Araç "yalnızca boş dosyalardan oluşan ağaç" durumunu da boş sayıyor; aynı
        // ölçüte bakılıyor ki kullanıcı süreci başlatıp hata beklemesin.
        if (!HasContent(artifact.BuildRoot))
        {
            issues.Add(new(IssueSeverity.Error,
                Loc.T("Epic.Validate.BuildRootEmpty", artifact.BuildRoot),
                nameof(artifact.BuildRoot)));
        }

        if (artifact.AppLaunch is { Length: > 0 } launch &&
            !File.Exists(Path.Combine(artifact.BuildRoot, launch)))
        {
            issues.Add(new(IssueSeverity.Error,
                Loc.T("Epic.Validate.AppLaunchMissing", launch),
                nameof(artifact.AppLaunch)));
        }
    }

    private static void ValidateGoLive(EpicArtifact artifact, List<ValidationIssue> issues)
    {
        // Etiket varsa platform şart: LabelBinary ikisini birlikte istiyor.
        if (artifact.Label is { Length: > 0 } && string.IsNullOrWhiteSpace(artifact.Platform))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Epic.Validate.PlatformRequired"),
                nameof(artifact.Platform)));
        }
    }

    /// <summary>
    /// Klasörde gerçekten veri var mı. Boş dosyalar sayılmıyor — aracın ölçütü de bu.
    /// </summary>
    internal static bool HasContent(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Any(file => new FileInfo(file).Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Okunamıyorsa boş olduğunu iddia etmiyoruz; araç kendi kararını verir.
            return true;
        }
    }
}
