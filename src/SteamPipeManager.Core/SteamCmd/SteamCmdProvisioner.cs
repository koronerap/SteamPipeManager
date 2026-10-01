using System.Formats.Tar;
using System.IO.Compression;

using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.SteamCmd;

public sealed record ProvisionProgress(string Stage, double? Fraction = null);

/// <summary>
/// SteamCMD kurulumunu hazırlar. Steamworks SDK'nın tamamı gerekmez: <c>run_app_build</c> için
/// Valve'ın herkese açık standalone steamcmd'si yeterlidir ve indirmesi kimlik doğrulaması istemez.
/// </summary>
public sealed class SteamCmdProvisioner(HttpClient? httpClient = null, HostPlatform? platform = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    private readonly HostPlatform _platform = platform ?? HostPlatform.Current;

    /// <summary>Bu platform için Valve'ın paket adresi.</summary>
    public Uri DownloadUrl => SteamCmdLayout.DownloadUrl(_platform);

    /// <summary>
    /// Kullanıcının gösterdiği yolu çözer: aracın kendisi, ContentBuilder ya da SDK kökü
    /// olabilir. Windows'ta <c>steamcmd.exe</c>, Linux/macOS'ta <c>steamcmd.sh</c> aranıyor;
    /// SDK'da her platformun ayrı builder klasörü var.
    /// </summary>
    public static string? LocateExecutable(string path, HostPlatform? platform = null)
    {
        platform ??= HostPlatform.Current;
        var name = SteamCmdLayout.ExecutableName(platform);
        var builder = SteamCmdLayout.BuilderFolder(platform);

        if (File.Exists(path))
        {
            return string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(path, name),
            Path.Combine(path, builder, name),
            Path.Combine(path, "tools", "ContentBuilder", builder, name),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// <paramref name="targetDirectory"/> içinde steamcmd yoksa indirir ve açar.
    /// Zaten varsa dokunmaz.
    /// </summary>
    public async Task<string> EnsureInstalledAsync(
        string targetDirectory,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var exePath = Path.Combine(targetDirectory, SteamCmdLayout.ExecutableName(_platform));

        if (File.Exists(exePath))
        {
            UnixPermissions.EnsureExecutable(exePath);
            return exePath;
        }

        Directory.CreateDirectory(targetDirectory);
        progress?.Report(new ProvisionProgress(Loc.T("SteamCmd.Downloading")));

        var archivePath = Path.Combine(targetDirectory, Path.GetFileName(DownloadUrl.AbsolutePath));

        try
        {
            await DownloadAsync(archivePath, progress, ct);

            progress?.Report(new ProvisionProgress(Loc.T("SteamCmd.Extracting")));
            Extract(archivePath, targetDirectory);
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }

        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(archivePath)} was extracted but {exePath} was not found.");
        }

        UnixPermissions.EnsureExecutable(exePath);
        return exePath;
    }

    /// <summary>
    /// Windows paketi zip, Linux/macOS paketleri tar.gz. Tar, çalıştırma izinlerini
    /// taşıyor; .NET Unix'te açarken onları uyguluyor.
    /// </summary>
    internal static void Extract(string archivePath, string targetDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, targetDirectory, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, targetDirectory, overwriteFiles: true);
    }

    private async Task DownloadAsync(string zipPath, IProgress<ProvisionProgress>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(zipPath);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;

            if (total is > 0)
            {
                progress?.Report(new ProvisionProgress(Loc.T("SteamCmd.Downloading"), (double)written / total.Value));
            }
        }
    }
}
