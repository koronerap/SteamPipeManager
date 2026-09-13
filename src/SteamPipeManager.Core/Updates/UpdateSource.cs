namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Güncellemelerin sorulduğu yer.
///
/// Sınama için adres ortam değişkeniyle değiştirilebiliyor; ama yalnızca HTTPS ya da
/// bu makinedeki (loopback) bir adrese. Aksi hâlde düz HTTP üzerinden, yol üstündeki
/// biri uygulamaya kendi paketini kurdurabilirdi.
/// </summary>
public sealed record UpdateSource(Uri ApiBase, string Repository)
{
    public const string DefaultRepository = "koronerap/SteamPipeManager";

    public const string ApiOverrideVariable = "SPM_UPDATE_API";

    public static readonly Uri GitHubApi = new("https://api.github.com/");

    public static UpdateSource Default => new(GitHubApi, DefaultRepository);

    public Uri LatestReleaseUrl => new(ApiBase, $"repos/{Repository}/releases/latest");

    public Uri ReleasesPageUrl => new($"https://github.com/{Repository}/releases/latest");

    public static UpdateSource Resolve(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;

        var value = readVariable(ApiOverrideVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        value = value.Trim();

        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrusted(uri)
            ? Default with { ApiBase = uri }
            : Default;
    }

    public static bool IsTrusted(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
