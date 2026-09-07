using SteamPipeManager.Core.Models;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.Vdf;

/// <summary>Bir app script'inden çıkarılan, kullanıcı onayına sunulacak öneri.</summary>
public sealed record ImportedSubApp(
    SubApp SubApp,
    string SourceAppScript,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Steamworks SDK ile birlikte gelen örnek script. Her SDK kopyasında bulunur,
    /// gerçek bir oyuna karşılık gelmez ve hepsi AppID 1000 kullandığı için içe
    /// aktarılırsa "aynı AppID iki kez" hatası üretir. Varsayılan olarak seçilmez.
    /// </summary>
    public bool IsSdkSample { get; init; }
}

public sealed record ImportResult(
    IReadOnlyList<ImportedSubApp> SubApps,
    IReadOnlyList<string> Warnings)
{
    public static readonly ImportResult Empty = new([], []);
}

/// <summary>
/// Mevcut <c>tools/ContentBuilder</c> kurulumlarını okuyup model ağacına dönüştürür.
/// Kullanıcının bugünkü kurulumlarını elle veri girişi olmadan aktarmayı sağlar.
/// </summary>
public static class ContentBuilderImporter
{
    /// <summary>SDK kökü, ContentBuilder klasörü veya doğrudan <c>scripts/</c> verilebilir.</summary>
    public static string? LocateScriptsDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        string[] candidates =
        [
            path,
            Path.Combine(path, "scripts"),
            Path.Combine(path, "tools", "ContentBuilder", "scripts"),
        ];

        return candidates.FirstOrDefault(
            c => Directory.Exists(c) && Directory.EnumerateFiles(c, "*.vdf").Any());
    }

    public static ImportResult ImportFrom(string path)
    {
        var scriptsDir = LocateScriptsDirectory(path);

        if (scriptsDir is null)
        {
            return new ImportResult([], [Loc.T("Import.NoScripts", path)]);
        }

        var imported = new List<ImportedSubApp>();
        var warnings = new List<string>();

        foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.vdf").Order())
        {
            VdfNode root;

            try
            {
                root = VdfParser.ParseSingleRootFile(file);
            }
            catch (Exception ex)
            {
                warnings.Add(Loc.T("Import.ReadFailed", Path.GetFileName(file), ex.Message));
                continue;
            }

            // Depot script'leri app script'lerinden referansla çözülür, tek başlarına atlanır.
            if (!string.Equals(root.Key, "appbuild", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryImportApp(root, file, scriptsDir, out var result))
            {
                imported.Add(result);
            }
            else
            {
                warnings.Add(Loc.T("Import.NoAppId", Path.GetFileName(file)));
            }
        }

        if (imported.Count == 0 && warnings.Count == 0)
        {
            warnings.Add(Loc.T("Import.NoAppScripts", scriptsDir));
        }

        return new ImportResult(imported, warnings);
    }

    private static bool TryImportApp(
        VdfNode root,
        string sourceFile,
        string scriptsDir,
        out ImportedSubApp result)
    {
        result = null!;

        if (root.UIntOf("appid") is not { } appId || appId == 0)
        {
            return false;
        }

        var warnings = new List<string>();
        var description = root.ValueOf("desc", "");

        var kind = GuessKind(description, sourceFile);

        var subApp = new SubApp
        {
            SteamAppId = appId,
            Title = TitleForKind(kind, appId),
            Kind = kind,
            SetLiveBranch = root.ValueOf("setlive", ""),
            Preview = root.BoolOf("preview"),
            ContentRoot = root.ValueOf("contentroot") is { Length: > 0 } cr ? cr : null,
            BuildDescriptionTemplate = description.Length > 0
                ? description
                : new SubApp().BuildDescriptionTemplate,
            Depots = [],
        };

        foreach (var entry in root.Child("depots")?.Children ?? [])
        {
            if (!uint.TryParse(entry.Key, out var depotId))
            {
                warnings.Add(Loc.T("Import.BadDepotId", entry.Key));
                continue;
            }

            subApp.Depots.Add(entry.IsBlock
                ? ImportInlineDepot(entry, depotId)
                : ImportReferencedDepot(entry.Value!, depotId, scriptsDir, warnings));
        }

        if (subApp.Depots.Count == 0)
        {
            warnings.Add(Loc.T("Import.NoDepots"));
        }

        var isSample = IsSdkSampleScript(sourceFile);

        if (isSample)
        {
            warnings.Add(Loc.T("Import.SdkSample"));
        }

        result = new ImportedSubApp(subApp, sourceFile, warnings) { IsSdkSample = isSample };
        return true;
    }

    /// <summary><c>simple_app_build.vdf</c>'teki gibi app script'ine gömülü depot bloğu.</summary>
    private static DepotConfig ImportInlineDepot(VdfNode block, uint depotId) =>
        ReadDepot(block, depotId);

    private static DepotConfig ImportReferencedDepot(
        string scriptReference,
        uint depotId,
        string scriptsDir,
        List<string> warnings)
    {
        // Referans mutlak ya da scripts/ klasörüne göreli olabilir.
        var candidate = Path.IsPathRooted(scriptReference)
            ? scriptReference
            : Path.Combine(scriptsDir, scriptReference);

        if (!File.Exists(candidate))
        {
            var byName = Path.Combine(scriptsDir, Path.GetFileName(scriptReference));

            if (File.Exists(byName))
            {
                candidate = byName;
            }
            else
            {
                warnings.Add(Loc.T("Import.DepotScriptMissing", depotId, scriptReference));
                return new DepotConfig { DepotId = depotId };
            }
        }

        try
        {
            return ReadDepot(VdfParser.ParseSingleRootFile(candidate), depotId);
        }
        catch (Exception ex)
        {
            warnings.Add(Loc.T("Import.DepotScriptUnreadable", depotId, Path.GetFileName(candidate), ex.Message));
            return new DepotConfig { DepotId = depotId };
        }
    }

    private static DepotConfig ReadDepot(VdfNode node, uint depotId)
    {
        var depot = new DepotConfig
        {
            DepotId = depotId,
            ContentRoot = node.ValueOf("contentroot") is { Length: > 0 } cr ? cr : null,
            InstallScript = node.ValueOf("InstallScript"),
            FileMappings = [],
            FileExclusions = [.. node.ChildrenNamed("FileExclusion").Select(n => n.Value!).Where(v => v.Length > 0)],
        };

        depot.Label = GuessLabel(depot.ContentRoot);

        foreach (var mapping in node.ChildrenNamed("FileMapping"))
        {
            depot.FileMappings.Add(new FileMapping
            {
                LocalPath = mapping.ValueOf("LocalPath", "*"),
                DepotPath = mapping.ValueOf("DepotPath", "."),
                Recursive = mapping.BoolOf("recursive", true),
            });
        }

        if (depot.FileMappings.Count == 0)
        {
            depot.FileMappings.Add(FileMapping.Everything());
        }

        foreach (var property in node.ChildrenNamed("FileProperties"))
        {
            depot.FileProperties.Add(new FileProperty
            {
                LocalPath = property.ValueOf("LocalPath", ""),
                Attributes = property.ValueOf("Attributes", ""),
            });
        }

        return depot;
    }

    /// <summary>İçerik klasörü adından platform etiketi tahmin eder (ör. <c>…\Skyward_Win</c> → "Windows").</summary>
    internal static string GuessLabel(string? contentRoot)
    {
        if (contentRoot is not { Length: > 0 })
        {
            return "";
        }

        var name = Path.GetFileName(contentRoot.TrimEnd('\\', '/'));

        return name switch
        {
            var n when Contains(n, "linux") => "Linux",
            var n when Contains(n, "mac") || Contains(n, "osx") => "macOS",
            var n when Contains(n, "win") => "Windows",
            _ => name,
        };

        static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Oyun adı için en iyi ipucu depot'un içerik klasörüdür: <c>…\content\Skyward_Win</c>
    /// → "Skyward". Klasör düz bir platform adıysa (<c>…\content\Windows</c>) hiçbir şey
    /// söylemez; o zaman ContentBuilder kurulumunun klasör adına düşülür
    /// (<c>D:\SteamPipe (Pixel Racer)</c> → "Pixel Racer").
    /// </summary>
    public static string SuggestAppTitle(ImportedSubApp imported)
    {
        var contentRoot = imported.SubApp.Depots
            .Select(d => d.ContentRoot)
            .FirstOrDefault(r => r is { Length: > 0 });

        if (contentRoot is not null)
        {
            var leaf = Path.GetFileName(contentRoot.TrimEnd('\\', '/'));

            if (StripPlatformSuffix(leaf) is { Length: > 0 } named)
            {
                return named;
            }
        }

        return InstallationName(imported.SourceAppScript) ?? $"App {imported.SubApp.SteamAppId}";
    }

    private static readonly string[] PlatformWords =
        ["windows", "win64", "win32", "win", "linux", "mac", "macos", "osx", "content", "depot"];

    /// <summary>Platform eki ayıklanır; geriye anlamlı bir ad kalmazsa boş döner.</summary>
    private static string StripPlatformSuffix(string folderName)
    {
        if (folderName.Length == 0)
        {
            return "";
        }

        // Klasörün tamamı platform adıysa oyun hakkında bilgi vermiyor demektir.
        if (PlatformWords.Contains(folderName, StringComparer.OrdinalIgnoreCase))
        {
            return "";
        }

        foreach (var separator in new[] { '_', '-', '.' })
        {
            var index = folderName.LastIndexOf(separator);

            if (index > 0 &&
                PlatformWords.Contains(folderName[(index + 1)..], StringComparer.OrdinalIgnoreCase))
            {
                return folderName[..index].Replace('_', ' ').Trim();
            }
        }

        return folderName;
    }

    /// <summary>ContentBuilder kurulumunun kök klasör adı; parantez içi varsa o tercih edilir.</summary>
    private static string? InstallationName(string sourceScript)
    {
        var dir = Path.GetDirectoryName(sourceScript);

        while (dir is not null)
        {
            var name = Path.GetFileName(dir);

            if (name.Length > 0 &&
                name is not ("scripts" or "ContentBuilder" or "tools" or "content"))
            {
                var start = name.IndexOf('(');
                var end = name.LastIndexOf(')');

                return start >= 0 && end > start ? name[(start + 1)..end].Trim() : name;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// SDK'nın dokümantasyon örnekleri her kurulumda bulunur ve hepsi AppID 1000 kullanır.
    /// </summary>
    internal static bool IsSdkSampleScript(string sourceFile)
    {
        var name = Path.GetFileName(sourceFile);

        return name.StartsWith("app_build_", StringComparison.OrdinalIgnoreCase)
            || name.Equals("simple_app_build.vdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>desc</c> alanı build notudur ("fix-update", "0.1.6 beta") — başlık olarak kullanılmaz.
    /// Türden türetilen ad çok daha anlamlı; build notu zaten açıklama şablonuna gidiyor.
    /// </summary>
    internal static string TitleForKind(SubAppKind kind, uint appId) => kind switch
    {
        SubAppKind.Main => Loc.T("Kind.Main"),
        SubAppKind.Demo => Loc.T("Kind.Demo"),
        SubAppKind.Playtest => Loc.T("Kind.Playtest"),
        SubAppKind.Beta => Loc.T("Kind.Beta"),
        SubAppKind.Tool => Loc.T("Kind.Tool"),
        _ => $"App {appId}",
    };

    internal static SubAppKind GuessKind(string description, string sourceFile)
    {
        var haystack = $"{description} {Path.GetFileNameWithoutExtension(sourceFile)}";

        if (Contains("demo"))
        {
            return SubAppKind.Demo;
        }

        if (Contains("playtest"))
        {
            return SubAppKind.Playtest;
        }

        if (Contains("beta"))
        {
            return SubAppKind.Beta;
        }

        return SubAppKind.Main;

        bool Contains(string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
