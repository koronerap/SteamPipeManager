using System.Security.Cryptography;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Paketi indirir ve yayındaki SHA-256 sağlamasıyla karşılaştırır.
///
/// İndirme önce <c>.part</c> adıyla yazılıyor ve sağlama tutana kadar asıl adını
/// almıyor; yarım kalan ya da bozulan bir dosya hiçbir zaman "hazır paket" gibi
/// görünmüyor.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, GitHubReleaseClient client)
{
    /// <summary>Ürün paketleri ~70 MB; bunun çok üstü yanlış bir dosya demek.</summary>
    private const long MaxPackageBytes = 1024L * 1024 * 1024;

    public async Task<string> DownloadAsync(
        UpdateCheckResult update,
        string directory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (update is not { Availability: UpdateAvailability.Available, Package: { } package, Checksums: { } checksums })
        {
            throw new InvalidOperationException("Update is not installable.");
        }

        if (!UpdateSource.IsTrusted(package.DownloadUrl))
        {
            throw new UpdateException(UpdateFailure.InvalidResponse, "Untrusted download address.");
        }

        var sums = ChecksumFile.Parse(await client.GetTextAsync(checksums.DownloadUrl, ct));

        if (!sums.TryGetValue(package.Name, out var expected))
        {
            throw new UpdateException(UpdateFailure.ChecksumMissing, $"{package.Name} is not listed in {checksums.Name}.");
        }

        Directory.CreateDirectory(directory);

        var final = Path.Combine(directory, package.Name);
        var partial = final + ".part";

        try
        {
            var actual = await DownloadToFileAsync(package, partial, progress, ct);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(
                    UpdateFailure.ChecksumMismatch,
                    $"Expected {expected}, downloaded {actual}.");
            }

            File.Move(partial, final, overwrite: true);
            return final;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private async Task<string> DownloadToFileAsync(
        ReleaseAsset package, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
        request.Headers.UserAgent.ParseAdd(GitHubReleaseClient.UserAgent);
        request.Headers.Accept.ParseAdd("application/octet-stream");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateFailure.Network, $"HTTP {(int)response.StatusCode}");
            }

            // Yönlendirme sonrası adres de güvenilir olmalı.
            if (response.RequestMessage?.RequestUri is { } finalUri && !UpdateSource.IsTrusted(finalUri))
            {
                throw new UpdateException(UpdateFailure.InvalidResponse, "Untrusted download address.");
            }

            var total = response.Content.Headers.ContentLength ?? (package.Size > 0 ? package.Size : 0);

            if (total > MaxPackageBytes)
            {
                throw new UpdateException(UpdateFailure.PackageInvalid, "Package is too large.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                received += read;

                if (received > MaxPackageBytes)
                {
                    throw new UpdateException(UpdateFailure.PackageInvalid, "Package is too large.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);

                if (total > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)received / total));
                }
            }

            progress?.Report(1.0);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Network, ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateFailure.Network, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, "Timed out.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bir sonraki indirme üzerine yazıyor.
        }
    }
}
