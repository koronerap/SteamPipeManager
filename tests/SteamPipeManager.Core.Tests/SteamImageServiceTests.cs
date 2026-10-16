using System.Net;
using System.Text;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Oyun görselleri. Steam yeni görselleri içerik özetli adreslere koyuyor; eski sabit
/// adres güncellenmiyor (CS2'de 2023'ten kalma görsel ölçüldü). Görsel de her açılışta
/// bir kez soruluyor ki mağazada değişen görsel uygulamaya da yansısın.
/// </summary>
public sealed class SteamImageServiceTests : IDisposable
{
    private const string Api = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/";
    private const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/730/";
    private const string LegacyLibrary = "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg";

    private static readonly byte[] Current = Enumerable.Repeat((byte)0xAB, 1024).ToArray();
    private static readonly byte[] Newer = Enumerable.Repeat((byte)0xCD, 1024).ToArray();
    private static readonly byte[] Stale = Enumerable.Repeat((byte)0xEF, 1024).ToArray();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_img_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string StoreResponse(string libraryFile, long version) =>
        $$$"""
        {"response":{"store_items":[{"item_type":0,"id":730,"success":1,"visible":true,"appid":730,
          "assets":{"asset_url_format":"steam/apps/730/${FILENAME}?t={{{version}}}",
                    "header":"0a0a/header.jpg","library_capsule":"{{{libraryFile}}}","last_modified":{{{version}}}}}]}}
        """;

    private const string NotOnStore =
        """{"response":{"store_items":[{"item_type":0,"id":730,"success":15,"visible":false,"name":"","appid":0}]}}""";

    private FakeSteam Steam(string? storeJson = null) => new() { StoreJson = storeJson ?? StoreResponse("1111/library_capsule.jpg", 1) };

    private SteamImageService Service(FakeSteam steam) => new(_dir, new HttpClient(steam));

    [Fact]
    public async Task The_current_store_image_is_used_not_the_stale_fixed_address()
    {
        var steam = Steam();
        steam.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        steam.Files[LegacyLibrary] = Stale;

        var path = await Service(steam).GetAppLibraryCapsuleAsync(730);

        Assert.NotNull(path);
        Assert.Equal(Current, await File.ReadAllBytesAsync(path!));
        Assert.DoesNotContain(LegacyLibrary, steam.Requests);
        Assert.Contains(steam.Requests, r => r.StartsWith(Api) && Uri.UnescapeDataString(r).Contains("\"appid\":730"));
    }

    [Fact]
    public async Task Steam_is_asked_once_per_launch()
    {
        var steam = Steam();
        steam.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var images = Service(steam);

        await images.GetAppLibraryCapsuleAsync(730);
        await images.GetAppLibraryCapsuleAsync(730);
        await images.GetAppCapsuleAsync(730);

        // Kütüphane görseli ve kapsül aynı mağaza yanıtından.
        Assert.Single(steam.Requests, r => r.StartsWith(Api));
        Assert.Single(steam.Requests, r => r.Contains("library_capsule"));
    }

    [Fact]
    public async Task An_unchanged_image_is_not_downloaded_again_on_the_next_launch()
    {
        var first = Steam();
        first.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var path = await Service(first).GetAppLibraryCapsuleAsync(730);

        var second = Steam();
        var again = await Service(second).GetAppLibraryCapsuleAsync(730);

        Assert.Equal(path, again);
        Assert.All(second.Requests, r => Assert.StartsWith(Api, r));
    }

    [Fact]
    public async Task A_changed_image_replaces_the_old_one_under_a_new_name()
    {
        var first = Steam();
        first.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var oldPath = await Service(first).GetAppLibraryCapsuleAsync(730);

        // Mağazada görsel değişti: yeni özet, yeni sürüm.
        var second = Steam(StoreResponse("2222/library_capsule.jpg", 2));
        second.Files[AssetBase + "2222/library_capsule.jpg?t=2"] = Newer;
        var newPath = await Service(second).GetAppLibraryCapsuleAsync(730);

        // Yeni ad: aynı yola yazılsaydı arayüzün görsel önbelleği eskisini gösterebilirdi.
        Assert.NotEqual(oldPath, newPath);
        Assert.Equal(Newer, await File.ReadAllBytesAsync(newPath!));
        Assert.False(File.Exists(oldPath));
        Assert.Equal(newPath, Service(Steam()).GetCachedAppLibraryCapsule(730));
    }

    [Fact]
    public async Task Without_Steam_the_cached_image_stays()
    {
        var first = Steam();
        first.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var path = await Service(first).GetAppLibraryCapsuleAsync(730);

        var offline = new FakeSteam { StoreStatus = HttpStatusCode.ServiceUnavailable };

        Assert.Equal(path, await Service(offline).GetAppLibraryCapsuleAsync(730));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_failed_download_keeps_the_cached_image()
    {
        var first = Steam();
        first.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var path = await Service(first).GetAppLibraryCapsuleAsync(730);

        // Yeni sürüm bildiriliyor ama görsel inmiyor.
        var second = Steam(StoreResponse("2222/library_capsule.jpg", 2));

        Assert.Equal(path, await Service(second).GetAppLibraryCapsuleAsync(730));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task An_app_not_on_the_store_falls_back_to_the_fixed_address()
    {
        var steam = Steam(NotOnStore);
        steam.Files[LegacyLibrary] = Current;

        var path = await Service(steam).GetAppLibraryCapsuleAsync(730);

        Assert.NotNull(path);
        Assert.Equal(Current, await File.ReadAllBytesAsync(path!));
    }

    [Fact]
    public async Task The_image_cached_by_older_versions_is_shown_and_then_replaced()
    {
        Directory.CreateDirectory(_dir);
        var legacy = Path.Combine(_dir, "lib_730.jpg");
        await File.WriteAllBytesAsync(legacy, Stale);
        await File.WriteAllTextAsync(legacy + ".missing", "");

        var steam = Steam();
        steam.Files[AssetBase + "1111/library_capsule.jpg?t=1"] = Current;
        var images = Service(steam);

        Assert.Equal(legacy, images.GetCachedAppLibraryCapsule(730));

        var path = await images.GetAppLibraryCapsuleAsync(730);

        Assert.Equal(Current, await File.ReadAllBytesAsync(path!));
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(legacy + ".missing"));
    }
}

/// <summary>Mağaza ucu ve görsel adresleri; istekleri kaydediyor.</summary>
internal sealed class FakeSteam : HttpMessageHandler
{
    public string? StoreJson { get; init; }

    public HttpStatusCode StoreStatus { get; init; } = HttpStatusCode.OK;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);

        HttpResponseMessage response;

        if (url.StartsWith("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/", StringComparison.Ordinal))
        {
            response = StoreStatus == HttpStatusCode.OK && StoreJson is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StoreJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(StoreStatus);
        }
        else
        {
            response = Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return Task.FromResult(response);
    }
}
