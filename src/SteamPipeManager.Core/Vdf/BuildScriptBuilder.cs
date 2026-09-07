using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Vdf;

/// <summary>Bir SubApp için üretilen script dosyalarının içeriği.</summary>
public sealed record GeneratedScripts(
    string AppScriptFileName,
    string AppScriptContent,
    IReadOnlyList<GeneratedDepotScript> DepotScripts);

public sealed record GeneratedDepotScript(uint DepotId, string FileName, string Content);

/// <summary>
/// Model nesnelerinden <c>app_&lt;appid&gt;.vdf</c> ve <c>depot_&lt;depotid&gt;.vdf</c> üretir.
/// Anahtar adları ve sıralaması <c>ref_scripts/</c> altındaki çalışan dosyalarla aynıdır.
/// </summary>
public static class BuildScriptBuilder
{
    /// <param name="depotScriptPaths">
    /// App script'inde depot dosyalarına verilecek yollar. Referans script'ler mutlak yol
    /// kullanıyor; workspace'te de öyle olacak.
    /// </param>
    public static VdfNode BuildAppScript(
        SubApp subApp,
        string buildOutputDirectory,
        IReadOnlyDictionary<uint, string> depotScriptPaths,
        string description,
        bool previewOverride = false)
    {
        ArgumentNullException.ThrowIfNull(subApp);

        var root = VdfNode.Block("appbuild");
        root.Add("appid", subApp.SteamAppId.ToString());
        root.Add("desc", description);
        root.Add("buildoutput", NormalizeDirectory(buildOutputDirectory));
        root.Add("contentroot", subApp.ContentRoot is { Length: > 0 } cr ? NormalizeDirectory(cr) : "");
        root.Add("setlive", subApp.SetLiveBranch ?? "");
        root.Add("preview", subApp.Preview || previewOverride ? "1" : "0");
        root.Add("local", "");

        var depots = VdfNode.Block("depots");

        foreach (var depot in subApp.Depots)
        {
            if (!depotScriptPaths.TryGetValue(depot.DepotId, out var scriptPath))
            {
                throw new InvalidOperationException(
                    $"No script path was supplied for depot {depot.DepotId}.");
            }

            depots.Add(depot.DepotId.ToString(), scriptPath);
        }

        root.Add(depots);
        return root;
    }

    public static VdfNode BuildDepotScript(DepotConfig depot, SubApp owner)
    {
        ArgumentNullException.ThrowIfNull(depot);
        ArgumentNullException.ThrowIfNull(owner);

        var root = VdfNode.Block("DepotBuildConfig");
        root.Add("DepotID", depot.DepotId.ToString());

        var contentRoot = ResolveContentRoot(depot, owner)
            ?? throw new InvalidOperationException(
                $"No content folder is set for depot {depot.DepotId}.");

        root.Add("contentroot", NormalizeDirectory(contentRoot));

        foreach (var mapping in depot.FileMappings)
        {
            var block = VdfNode.Block("FileMapping");
            block.Add("LocalPath", mapping.LocalPath);
            block.Add("DepotPath", mapping.DepotPath);
            block.Add("recursive", mapping.Recursive ? "1" : "0");
            root.Add(block);
        }

        foreach (var exclusion in depot.FileExclusions)
        {
            root.Add("FileExclusion", exclusion);
        }

        if (depot.InstallScript is { Length: > 0 } installScript)
        {
            root.Add("InstallScript", installScript);
        }

        foreach (var property in depot.FileProperties)
        {
            var block = VdfNode.Block("FileProperties");
            block.Add("LocalPath", property.LocalPath);
            block.Add("Attributes", property.Attributes);
            root.Add(block);
        }

        return root;
    }

    /// <summary>Depot kendi içerik kökünü tanımlamamışsa SubApp'inkine düşer.</summary>
    public static string? ResolveContentRoot(DepotConfig depot, SubApp owner) =>
        depot.ContentRoot is { Length: > 0 } depotRoot
            ? depotRoot
            : owner.ContentRoot is { Length: > 0 } appRoot ? appRoot : null;

    /// <summary>
    /// Referans script'lerde klasör yolları sondaki ters bölü olmadan yazılmış
    /// (<c>"D:\sdk\tools\ContentBuilder\output"</c>), aynısı yapılır.
    /// </summary>
    private static string NormalizeDirectory(string path)
    {
        var trimmed = path.TrimEnd();
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }
}
