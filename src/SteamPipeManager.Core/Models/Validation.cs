using SteamPipeManager.Core.Vdf;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.Models;

public enum IssueSeverity
{
    /// <summary>Kaydetmeyi engellemez ama build'i engeller.</summary>
    Warning,

    /// <summary>Kaydetmeyi de engeller.</summary>
    Error,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Message, string? Field = null)
{
    public override string ToString() => Field is null ? Message : $"{Field}: {Message}";
}

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public static readonly ValidationResult Ok = new([]);

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    public bool CanSave => !HasErrors;

    public bool CanBuild => Issues.Count == 0;

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// Kaydetme ve build öncesi kontroller. Diske dokunan kontroller (klasör var mı, boş mu)
/// <paramref name="checkFileSystem"/> ile ayrılabilir; testler bu sayede sahte yol kullanabilir.
/// </summary>
public static class ProfileValidator
{
    /// <summary>Steam varsayılan branch'e doğrudan build alınmasına izin vermiyor.</summary>
    public const string ForbiddenBranch = "default";

    public static ValidationResult ValidateProfile(UserProfile profile, bool checkFileSystem = true)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Validate.ProfileNameRequired"), nameof(profile.DisplayName)));
        }

        if (string.IsNullOrWhiteSpace(profile.SteamUsername))
        {
            issues.Add(new(IssueSeverity.Error, Loc.T("Validate.UsernameRequired"), nameof(profile.SteamUsername)));
        }

        var appIds = new Dictionary<uint, string>();
        var depotIds = new Dictionary<uint, string>();

        foreach (var app in profile.Apps)
        {
            issues.AddRange(ValidateApp(app, appIds, depotIds, checkFileSystem));
        }

        return new ValidationResult(issues);
    }

    public static ValidationResult ValidateSubApp(SubApp subApp, bool checkFileSystem = true) =>
        new([.. ValidateSubAppCore(subApp, subAppLabel: subApp.Title, new(), new(), checkFileSystem)]);

    private static IEnumerable<ValidationIssue> ValidateApp(
        SteamApp app,
        Dictionary<uint, string> appIds,
        Dictionary<uint, string> depotIds,
        bool checkFileSystem)
    {
        if (string.IsNullOrWhiteSpace(app.Title))
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.GameNameRequired"));
        }

        if (app.SubApps.Count == 0)
        {
            yield return new(IssueSeverity.Warning, Loc.T("Validate.NoTargets", app.Title));
        }

        var mainCount = app.SubApps.Count(s => s.Kind == SubAppKind.Main);

        if (mainCount > 1)
        {
            yield return new(
                IssueSeverity.Error,
                Loc.T("Validate.TooManyMain", app.Title, mainCount));
        }

        foreach (var subApp in app.SubApps)
        {
            foreach (var issue in ValidateSubAppCore(
                         subApp, $"{app.Title} / {subApp.Title}", appIds, depotIds, checkFileSystem))
            {
                yield return issue;
            }
        }
    }

    private static IEnumerable<ValidationIssue> ValidateSubAppCore(
        SubApp subApp,
        string subAppLabel,
        Dictionary<uint, string> appIds,
        Dictionary<uint, string> depotIds,
        bool checkFileSystem)
    {
        if (string.IsNullOrWhiteSpace(subApp.Title))
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.TargetNameRequired"));
        }

        if (subApp.SteamAppId == 0)
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.AppIdRequired", subAppLabel));
        }
        else if (!appIds.TryAdd(subApp.SteamAppId, subAppLabel))
        {
            yield return new(
                IssueSeverity.Error,
                Loc.T("Validate.DuplicateAppId", subApp.SteamAppId, appIds[subApp.SteamAppId], subAppLabel));
        }

        if (string.Equals(subApp.SetLiveBranch?.Trim(), ForbiddenBranch, StringComparison.OrdinalIgnoreCase))
        {
            yield return new(
                IssueSeverity.Error,
                Loc.T("Validate.ForbiddenBranch", subAppLabel, ForbiddenBranch),
                nameof(subApp.SetLiveBranch));
        }

        if (subApp.Depots.Count == 0)
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.DepotRequired", subAppLabel));
        }

        foreach (var depot in subApp.Depots)
        {
            foreach (var issue in ValidateDepot(depot, subApp, subAppLabel, depotIds, checkFileSystem))
            {
                yield return issue;
            }
        }
    }

    private static IEnumerable<ValidationIssue> ValidateDepot(
        DepotConfig depot,
        SubApp owner,
        string subAppLabel,
        Dictionary<uint, string> depotIds,
        bool checkFileSystem)
    {
        var label = $"{subAppLabel} / depot {depot.DepotId}";

        if (depot.DepotId == 0)
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.DepotIdRequired", subAppLabel));
            yield break;
        }

        if (!depotIds.TryAdd(depot.DepotId, label))
        {
            yield return new(
                IssueSeverity.Error,
                Loc.T("Validate.DuplicateDepotId", depot.DepotId, depotIds[depot.DepotId], label));
        }

        if (depot.FileMappings.Count == 0)
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.MappingRequired", label));
        }

        var contentRoot = BuildScriptBuilder.ResolveContentRoot(depot, owner);

        if (contentRoot is null)
        {
            yield return new(IssueSeverity.Error, Loc.T("Validate.NoContentRoot", label));
            yield break;
        }

        if (!checkFileSystem)
        {
            yield break;
        }

        if (!Directory.Exists(contentRoot))
        {
            yield return new(IssueSeverity.Warning, Loc.T("Validate.ContentMissing", label, contentRoot));
            yield break;
        }

        // Boş bir depot yüklemek Steam'de yayında olan içeriği siler; bu yüzden build engellenir.
        if (!Directory.EnumerateFileSystemEntries(contentRoot).Any())
        {
            yield return new(
                IssueSeverity.Warning,
                Loc.T("Validate.ContentEmpty", label, contentRoot));
        }
    }
}
