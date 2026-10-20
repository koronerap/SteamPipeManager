using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Arayüzlerde kullanılan simge adları gerçekten var mı. Ad metin olarak yazılıyor ve
/// ancak çalışırken çözülüyor: derleme geçiyor, uygulama pencere açılırken çöküyor.
/// "PanelBottom24" WPF-UI'de yoktu ve Hub hiç açılmadı; bu testler onu yakalardı.
///
/// Simge kütüphaneleri NuGet önbelleğinden okunuyor; projesi geri yüklenmemiş bir
/// makinede (ör. CI'daki Linux test işinde WPF) test atlanıyor.
/// </summary>
public sealed class IconNameTests
{
    [IconLibraryFact("src/SteamPipeManager.App/SteamPipeManager.App.csproj", "WPF-UI", "WPF-UI", "Wpf.Ui.dll")]
    public void Every_icon_in_the_windows_app_exists_in_WPF_UI()
    {
        var library = IconLibrary.Find("src/SteamPipeManager.App/SteamPipeManager.App.csproj", "WPF-UI", "WPF-UI", "Wpf.Ui.dll")!;
        var known = IconLibrary.EnumNames(library, "SymbolRegular");

        var used = IconLibrary.UsedNames("src/SteamPipeManager.App", ".xaml", @"SymbolIcon (\w+)\}|Symbol=""(\w+)""");

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(u => !known.Contains(u.Name)).Select(u => $"{u.Name} ({u.File})"));
    }

    [IconLibraryFact("src/SteamPipeManager.Desktop/SteamPipeManager.Desktop.csproj", "FluentIcons.Avalonia", "FluentIcons.Common", "FluentIcons.Common.dll")]
    public void Every_icon_in_the_linux_and_macos_app_exists_in_FluentIcons()
    {
        var library = IconLibrary.Find("src/SteamPipeManager.Desktop/SteamPipeManager.Desktop.csproj", "FluentIcons.Avalonia", "FluentIcons.Common", "FluentIcons.Common.dll")!;
        var known = IconLibrary.EnumNames(library, "Symbol");

        var used = IconLibrary.UsedNames("src/SteamPipeManager.Desktop", ".axaml", @"Symbol=""(\w+)""");

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(u => !known.Contains(u.Name)).Select(u => $"{u.Name} ({u.File})"));
    }
}

/// <summary>Simge kütüphanesi bu makinede yoksa test atlanıyor.</summary>
public sealed class IconLibraryFactAttribute : FactAttribute
{
    public IconLibraryFactAttribute(string project, string versionOf, string package, string assembly)
    {
        if (IconLibrary.Find(project, versionOf, package, assembly) is null)
        {
            Skip = $"{package} bu makinede geri yüklenmemiş.";
        }
    }
}

internal static class IconLibrary
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);

        while (dir is not null && !File.Exists(Path.Combine(dir, "SteamPipeManager.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? ".";
    }

    /// <summary>
    /// Paketin projede kullanılan sürümündeki derleme. <paramref name="versionOf"/>: sürümün
    /// okunduğu paket (FluentIcons.Common, FluentIcons.Avalonia ile aynı sürümde geliyor).
    /// </summary>
    public static string? Find(string project, string versionOf, string package, string assembly)
    {
        var projectFile = Path.Combine(RepoRoot(), project);

        if (!File.Exists(projectFile))
        {
            return null;
        }

        var version = Regex.Match(File.ReadAllText(projectFile), $@"Include=""{Regex.Escape(versionOf)}"" Version=""([^""]+)""").Groups[1].Value;

        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var lib = Path.Combine(packages, package.ToLowerInvariant(), version, "lib");

        return version.Length > 0 && Directory.Exists(lib)
            ? Directory.EnumerateFiles(lib, assembly, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    /// <summary>Bir enum'un alan adları, derleme yüklenmeden üst veriden okunarak.</summary>
    public static HashSet<string> EnumNames(string assembly, string enumName)
    {
        using var pe = new PEReader(File.OpenRead(assembly));
        var md = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);

            if (md.GetString(type.Name) == enumName)
            {
                foreach (var field in type.GetFields())
                {
                    names.Add(md.GetString(md.GetFieldDefinition(field).Name));
                }
            }
        }

        return names;
    }

    public static List<(string Name, string File)> UsedNames(string folder, string extension, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*" + extension, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), pattern)
                .Select(m => (Name: m.Groups.Values.Skip(1).First(g => g.Success).Value, File: Path.GetFileName(f))))
            .Distinct()
            .ToList();
}
