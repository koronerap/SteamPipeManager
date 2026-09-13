using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// GitHub Releases API'sinden son kararlı sürümü okur.
///
/// <c>releases/latest</c> taslakları ve önsürümleri zaten döndürmüyor; böylece
/// kullanıcılar yalnızca yayımlandı olarak işaretlenen sürüme güncelleniyor.
/// Kimlik doğrulaması yok: uygulama başına bir sorgu, IP başına saatlik sınırın
/// çok altında.
/// </summary>
public sealed class GitHubReleaseClient(HttpClient http, UpdateSource source)
{
    public const string UserAgent = "SteamPipeManager-Updater";

    /// <summary>Sağlama dosyası birkaç satır; bundan büyüğü yanlış bir dosya demek.</summary>
    private const long MaxTextBytes = 64 * 1024;

    public UpdateSource Source => source;

    /// <summary>Hiç yayın yoksa null.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Network, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, "Timed out.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateException(UpdateFailure.RateLimited, $"HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateFailure.Network, $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                return Parse(document.RootElement)
                       ?? throw new UpdateException(UpdateFailure.InvalidResponse, "Release has no usable version tag.");
            }
            catch (JsonException ex)
            {
                throw new UpdateException(UpdateFailure.InvalidResponse, ex.Message, ex);
            }
        }
    }

    /// <summary>API yanıtını çözer; etiket sürüm olarak okunamıyorsa null.</summary>
    public static ReleaseInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.GetString() is not { Length: > 0 } tag ||
            AppVersion.TryParse(tag) is not { } version)
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();

        if (root.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetList.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } assetName &&
                    asset.TryGetProperty("browser_download_url", out var url) &&
                    Uri.TryCreate(url.GetString(), UriKind.Absolute, out var downloadUrl))
                {
                    var size = asset.TryGetProperty("size", out var sizeElement) &&
                               sizeElement.TryGetInt64(out var bytes)
                        ? bytes
                        : 0;

                    assets.Add(new ReleaseAsset(assetName, downloadUrl, size));
                }
            }
        }

        Uri? page = null;
        if (root.TryGetProperty("html_url", out var html) &&
            Uri.TryCreate(html.GetString(), UriKind.Absolute, out var htmlUrl) &&
            htmlUrl.Scheme == Uri.UriSchemeHttps)
        {
            page = htmlUrl;
        }

        var title = root.TryGetProperty("name", out var titleElement) ? titleElement.GetString() : null;

        return new ReleaseInfo(tag, version, title, page, assets);
    }

    /// <summary>Küçük bir metin dosyasını (sağlama listesi) indirir.</summary>
    public async Task<string> GetTextAsync(Uri url, CancellationToken ct = default)
    {
        if (!UpdateSource.IsTrusted(url))
        {
            throw new UpdateException(UpdateFailure.InvalidResponse, "Untrusted download address.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateFailure.Network, $"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxTextBytes)
            {
                throw new UpdateException(UpdateFailure.InvalidResponse, "Checksum file is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;

            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);

                if (buffer.Length > MaxTextBytes)
                {
                    throw new UpdateException(UpdateFailure.InvalidResponse, "Checksum file is too large.");
                }
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Network, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, "Timed out.", ex);
        }
    }
}
