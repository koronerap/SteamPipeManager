using System.Runtime.CompilerServices;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// <c>tests/fixtures/content_builder/</c> altındaki referans script'lere erişim.
///
/// Bunlar gerçek bir ContentBuilder kurulumunun bayt biçimini taklit eder: CRLF satır
/// sonu, BOM yok, sonda satır sonu yok, sekme girintisi. VDF ayrıştırıcısı ve yazıcısı
/// bu dosyalara göre ayarlandığı için biçimleri anlamlı — bkz. yanındaki README.
/// </summary>
internal static class RefScripts
{
    public static string Directory { get; } = FindDirectory();

    public static string Path(string fileName) => System.IO.Path.Combine(Directory, fileName);

    public static string Read(string fileName) => File.ReadAllText(Path(fileName));

    public static IEnumerable<string> AppScriptFiles() =>
        System.IO.Directory.EnumerateFiles(Directory, "app_*.vdf")
            .Where(f => !System.IO.Path.GetFileName(f).StartsWith("app_build_", StringComparison.Ordinal))
            .Order();

    public static IEnumerable<string> DepotScriptFiles() =>
        System.IO.Directory.EnumerateFiles(Directory, "depot_*.vdf")
            .Where(f => !System.IO.Path.GetFileName(f).StartsWith("depot_build_", StringComparison.Ordinal))
            .Order();

    /// <summary>
    /// Test derlemesi <c>bin/</c> altından çalıştığı için klasör kaynak dosya konumundan bulunur;
    /// böylece dosyaları çıktıya kopyalamaya gerek kalmaz.
    /// </summary>
    private static string FindDirectory([CallerFilePath] string callerPath = "")
    {
        var dir = System.IO.Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir, "tests", "fixtures", "content_builder");

            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "tests/fixtures/content_builder klasörü bulunamadı.");
    }
}
