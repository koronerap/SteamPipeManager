using System.Runtime.CompilerServices;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// <c>tests/fixtures/</c> altındaki örnek dosyalar: referans ContentBuilder script'leri ve
/// gerçek araç çalıştırmalarının log'ları.
///
/// Bu dosyalar geliştirme sırasında kullanılıyor ama depoda <b>yok</b> (bkz. .gitignore).
/// Bulunmadıklarında onlara dayanan testler başarısız olmuyor, "atlandı" olarak
/// raporlanıyor; diğer testler her makinede çalışıyor.
/// </summary>
internal static class Fixtures
{
    public const string ContentBuilder = "content_builder";

    public const string BptChunkLog = "real_bpt_chunk_generation.log";

    public const string SteamCmdPreviewLog = "real_preview_build_console_log.txt";

    public static string Root { get; } = FindRoot();

    public static string PathOf(string name) => System.IO.Path.Combine(Root, name);

    public static bool Exists(string name) =>
        File.Exists(PathOf(name)) || Directory.Exists(PathOf(name));

    public static string SkipReason(string name) =>
        Exists(name) ? null! : $"tests/fixtures/{name} yok; örnek dosyalar depoya dahil değil.";

    /// <summary>Depo kökü, çözüm dosyasından bulunuyor; fixture klasörü olmasa da yol belli.</summary>
    private static string FindRoot([CallerFilePath] string callerPath = "")
    {
        var dir = System.IO.Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir, "SteamPipeManager.sln")))
            {
                return System.IO.Path.Combine(dir, "tests", "fixtures");
            }

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(callerPath) ?? ".", "..", "fixtures");
    }
}

/// <summary>Örnek dosya yoksa atlanan <see cref="FactAttribute"/>.</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string fixture)
    {
        if (!Fixtures.Exists(fixture))
        {
            Skip = Fixtures.SkipReason(fixture);
        }
    }
}

/// <summary>Örnek dosya yoksa atlanan <see cref="TheoryAttribute"/>; veri de okunmaz.</summary>
public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute(string fixture)
    {
        if (!Fixtures.Exists(fixture))
        {
            Skip = Fixtures.SkipReason(fixture);
        }
    }
}
