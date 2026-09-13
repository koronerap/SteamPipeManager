using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(
    string Tag,
    AppVersion Version,
    string? Title,
    Uri? PageUrl,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

public enum UpdateAvailability
{
    UpToDate,

    /// <summary>Paket ve sağlama dosyası var; uygulama içinden kurulabilir.</summary>
    Available,

    /// <summary>
    /// Daha yeni bir sürüm var ama uygulama içinden kurulamıyor — ör. bu ürünün
    /// paketi ya da sağlama dosyası yayında yok. Kullanıcıya indirme sayfası gösterilir.
    /// </summary>
    ManualOnly,
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    AppVersion Current,
    ReleaseInfo? Release = null,
    ReleaseAsset? Package = null,
    ReleaseAsset? Checksums = null);

public enum UpdateFailure
{
    Network,
    RateLimited,
    InvalidResponse,
    ChecksumMissing,
    ChecksumMismatch,
    PackageInvalid,
    InstallFailed,
    RestartFailed,
}

/// <summary>
/// Güncelleyicinin kullanıcıya anlatılabilir hataları. Sebep bir sıralama değeri;
/// metin arayüzde, seçili dilde üretiliyor.
/// </summary>
public sealed class UpdateException(UpdateFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateFailure Failure { get; } = failure;
}

/// <summary>Yayındaki dosya adları. <c>publish.ps1</c> ile aynı sözleşme.</summary>
public static class UpdateAssets
{
    public const string Runtime = "win-x64";

    public const string ChecksumFileName = "SHA256SUMS.txt";

    public static string PackageName(ProductId product) => $"{product}-{Runtime}.zip";

    public static string ExecutableName(ProductId product) => $"{product}.exe";
}
