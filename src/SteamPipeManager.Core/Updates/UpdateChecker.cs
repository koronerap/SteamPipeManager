using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Yeni sürüm var mı ve bu ürün için uygulama içinden kurulabilir mi.
///
/// Üç ürün aynı yayını paylaşıyor; her biri yalnızca kendi paketine bakıyor.
/// Paket ya da sağlama dosyası yoksa sonuç "elle indir" oluyor — sağlaması
/// doğrulanamayan bir paket hiçbir koşulda kurulmuyor.
/// </summary>
public sealed class UpdateChecker(GitHubReleaseClient client, ProductId product)
{
    public async Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken ct = default)
    {
        var release = await client.GetLatestAsync(ct);

        if (release is null || !release.Version.IsNewerThan(current))
        {
            return new UpdateCheckResult(UpdateAvailability.UpToDate, current, release);
        }

        var package = release.FindAsset(UpdateAssets.PackageName(product));
        var checksums = release.FindAsset(UpdateAssets.ChecksumFileName);

        var installable =
            package is not null && checksums is not null &&
            UpdateSource.IsTrusted(package.DownloadUrl) &&
            UpdateSource.IsTrusted(checksums.DownloadUrl);

        return new UpdateCheckResult(
            installable ? UpdateAvailability.Available : UpdateAvailability.ManualOnly,
            current,
            release,
            installable ? package : null,
            installable ? checksums : null);
    }
}
