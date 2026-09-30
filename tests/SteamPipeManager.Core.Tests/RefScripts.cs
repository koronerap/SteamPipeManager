namespace SteamPipeManager.Core.Tests;

/// <summary>
/// <c>tests/fixtures/content_builder/</c> altındaki referans script'lere erişim.
///
/// Bunlar gerçek bir ContentBuilder kurulumunun bayt biçimini taklit eder: CRLF satır
/// sonu, BOM yok, sonda satır sonu yok, sekme girintisi. VDF ayrıştırıcısı ve yazıcısı
/// bu dosyalara göre ayarlandığı için biçimleri anlamlı — bkz. yanındaki README.
/// Klasör depoya dahil değil; yoksa ona dayanan testler atlanıyor.
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
    /// Klasör depoda olmayabilir (bkz. <see cref="Fixtures"/>); o zaman buna dayanan
    /// testler zaten atlanıyor ve bu yola hiç dokunulmuyor.
    /// </summary>
    private static string FindDirectory() => Fixtures.PathOf(Fixtures.ContentBuilder);
}
