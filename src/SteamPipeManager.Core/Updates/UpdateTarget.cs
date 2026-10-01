using SteamPipeManager.Core.Platform;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Hangi ürünün hangi platform için paketi güncelleniyor ve paketin içi nasıl.
/// <c>publish.ps1</c> ile aynı sözleşme.
///
/// | Platform | Paket | Uygulama kökü | Çalıştırılan dosya |
/// |---|---|---|---|
/// | Windows | <c>Ürün-win-x64.zip</c> | exe'nin klasörü | <c>Ürün.exe</c> |
/// | Linux | <c>Ürün-linux-x64.tar.gz</c> | ikilinin klasörü | <c>Ürün</c> |
/// | macOS | <c>Ürün-osx-arm64.tar.gz</c> | <c>Ürün Adı.app</c> | <c>Contents/MacOS/Ürün</c> |
///
/// Linux ve macOS paketleri tar.gz, çünkü zip çalıştırma iznini taşımıyor: izinsiz
/// açılan bir ikili hiç başlamaz.
/// </summary>
public sealed record UpdateTarget(ProductId Product, string ProductName, HostPlatform Platform)
{
    public static UpdateTarget For(ProductProfile product, HostPlatform? platform = null) =>
        new(product.Id, product.Name, platform ?? HostPlatform.Current);

    public string PackageName => Platform.IsWindows
        ? $"{Product}-{Platform.RuntimeId}.zip"
        : $"{Product}-{Platform.RuntimeId}.tar.gz";

    public string ExecutableFileName => Platform.IsWindows ? $"{Product}.exe" : Product.ToString();

    /// <summary>macOS uygulama paketinin adı: Finder'da görünen ad.</summary>
    public string BundleName => $"{ProductName}.app";

    /// <summary>Uygulama kökünden çalıştırılan dosyaya göreli yol.</summary>
    public string ExecutableRelativePath => Platform.IsMac
        ? Path.Combine("Contents", "MacOS", ExecutableFileName)
        : ExecutableFileName;

    /// <summary>
    /// Çalışan dosyadan uygulama kökünü çıkarır. Düzen yayın paketininkiyle uyuşmuyorsa
    /// (ör. geliştirme çıktısı) null: oraya paket açmak geliştirme klasörünü bozardı.
    /// </summary>
    public string? AppRootOf(string? runningExecutable)
    {
        if (runningExecutable is not { Length: > 0 })
        {
            return null;
        }

        var comparison = Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!string.Equals(Path.GetFileName(runningExecutable), ExecutableFileName, comparison))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(runningExecutable);

        if (!Platform.IsMac)
        {
            return directory is { Length: > 0 } ? directory : null;
        }

        // .../Ad.app/Contents/MacOS/İkili
        var macOs = directory is null ? null : new DirectoryInfo(directory);
        var contents = macOs?.Parent;
        var bundle = contents?.Parent;

        return macOs?.Name == "MacOS" && contents?.Name == "Contents" &&
               bundle is not null && bundle.Name.EndsWith(".app", StringComparison.Ordinal)
            ? bundle.FullName
            : null;
    }
}
