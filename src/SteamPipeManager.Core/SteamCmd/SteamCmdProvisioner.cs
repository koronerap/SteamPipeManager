using System.IO.Compression;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.SteamCmd;

public sealed record ProvisionProgress(string Stage, double? Fraction = null);

/// <summary>
/// SteamCMD kurulumunu hazırlar. Steamworks SDK'nın tamamı gerekmez: <c>run_app_build</c> için
/// Valve'ın herkese açık standalone steamcmd'si yeterlidir ve indirmesi kimlik doğrulaması istemez.
/// </summary>
public sealed class SteamCmdProvisioner(HttpClient? httpClient = null)
{
    public const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <summary>Kullanıcının gösterdiği yolu çözer: exe'nin kendisi, ContentBuilder ya da SDK kökü olabilir.</summary>
    public static string? LocateExecutable(string path)
    {
        if (File.Exists(path))
        {
            return string.Equals(Path.GetFileName(path), "steamcmd.exe", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(path, "steamcmd.exe"),
            Path.Combine(path, "builder", "steamcmd.exe"),
            Path.Combine(path, "tools", "ContentBuilder", "builder", "steamcmd.exe"),
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
        var exePath = Path.Combine(targetDirectory, "steamcmd.exe");

        if (File.Exists(exePath))
        {
            return exePath;
        }

        Directory.CreateDirectory(targetDirectory);
        progress?.Report(new ProvisionProgress(Loc.T("SteamCmd.Downloading")));

        var zipPath = Path.Combine(targetDirectory, "steamcmd.zip");

        try
        {
            await DownloadAsync(zipPath, progress, ct);

            progress?.Report(new ProvisionProgress(Loc.T("SteamCmd.Extracting")));
            ZipFile.ExtractToDirectory(zipPath, targetDirectory, overwriteFiles: true);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }

        return File.Exists(exePath)
            ? exePath
            : throw new InvalidOperationException(
                $"steamcmd.zip was extracted but {exePath} was not found.");
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
