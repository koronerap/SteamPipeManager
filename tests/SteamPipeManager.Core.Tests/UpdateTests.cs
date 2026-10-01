using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.Updates;

namespace SteamPipeManager.Core.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v1.0.2", 1, 0, 2, null)]
    [InlineData("1.0.2", 1, 0, 2, null)]
    [InlineData("1.0.1.0", 1, 0, 1, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1.0.2+4f2c1ab", 1, 0, 2, null)]
    [InlineData("V2.0.0-beta.1", 2, 0, 0, "beta.1")]
    public void Parses_the_forms_releases_and_assemblies_use(string text, int major, int minor, int patch, string? pre)
    {
        Assert.Equal(new AppVersion(major, minor, patch, pre), AppVersion.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.0.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0.0.0")]
    [InlineData("-1.0.0")]
    public void Rejects_what_is_not_a_version(string? text)
    {
        Assert.Null(AppVersion.TryParse(text));
    }

    [Theory]
    [InlineData("1.0.2", "1.0.1")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.1.0", "1.1.0-beta")]
    [InlineData("1.1.0-beta.2", "1.1.0-beta.1")]
    [InlineData("1.1.0-beta.10", "1.1.0-beta.9")]
    [InlineData("1.1.0-rc", "1.1.0-beta")]
    [InlineData("1.1.0-beta.1", "1.1.0-beta")]
    public void Orders_versions(string newer, string older)
    {
        Assert.True(AppVersion.TryParse(newer)!.IsNewerThan(AppVersion.TryParse(older)!));
        Assert.False(AppVersion.TryParse(older)!.IsNewerThan(AppVersion.TryParse(newer)!));
    }

    [Fact]
    public void Build_metadata_does_not_make_a_version_newer()
    {
        Assert.False(AppVersion.TryParse("1.0.1+abc")!.IsNewerThan(AppVersion.TryParse("1.0.1.0")!));
    }
}

public sealed class UpdateSourceTests
{
    [Fact]
    public void Defaults_to_GitHub()
    {
        var source = UpdateSource.Resolve(_ => null);

        Assert.Equal(
            "https://api.github.com/repos/koronerap/SteamPipeManager/releases/latest",
            source.LatestReleaseUrl.ToString());
    }

    [Theory]
    [InlineData("https://mirror.example.com/api", "https://mirror.example.com/api/")]
    [InlineData("http://127.0.0.1:8765", "http://127.0.0.1:8765/")]
    [InlineData("http://localhost:8765/", "http://localhost:8765/")]
    public void Accepts_https_or_loopback_overrides(string value, string expected)
    {
        Assert.Equal(expected, UpdateSource.Resolve(_ => value).ApiBase.ToString());
    }

    [Theory]
    [InlineData("http://updates.example.com/")]
    [InlineData("file:///C:/updates/")]
    [InlineData("not a url")]
    public void Ignores_overrides_that_could_serve_a_forged_package(string value)
    {
        Assert.Equal(UpdateSource.GitHubApi, UpdateSource.Resolve(_ => value).ApiBase);
    }
}

public sealed class ChecksumFileTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HashB = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void Reads_sha256sum_output_including_binary_markers_BOM_and_CRLF()
    {
        var text = "\uFEFF" + HashA + "  SteamPipeManager-win-x64.zip\r\n" +
                   HashB + " *PipeManagerHub-win-x64.zip\r\n" +
                   "# comment\r\n\r\n" +
                   "tooshort  Other.zip\r\n";

        var sums = ChecksumFile.Parse(text);

        Assert.Equal(2, sums.Count);
        Assert.Equal(HashA, sums["SteamPipeManager-win-x64.zip"]);
        Assert.Equal(HashB.ToLowerInvariant(), sums["pipemanagerhub-win-x64.zip"]);
    }

    [Fact]
    public void Format_round_trips()
    {
        var line = ChecksumFile.Format(HashB, "EpicBuildManager-win-x64.zip");

        Assert.Equal(HashB.ToLowerInvariant(), ChecksumFile.Parse(line)["EpicBuildManager-win-x64.zip"]);
    }
}

public sealed class UpdateLaunchArgumentsTests
{
    [Fact]
    public void Round_trips_through_the_command_line()
    {
        var text = UpdateLaunchArguments.Build(new AppVersion(1, 0, 1), 4242);
        var parsed = UpdateLaunchArguments.Parse(text.Split(' '));

        Assert.Equal(new AppVersion(1, 0, 1), parsed.UpdatedFrom);
        Assert.Equal(4242, parsed.WaitForProcessId);
    }

    [Fact]
    public void A_normal_start_carries_nothing()
    {
        var parsed = UpdateLaunchArguments.Parse(["--something", "--wait-pid=abc", "--wait-pid=-3"]);

        Assert.Null(parsed.UpdatedFrom);
        Assert.Null(parsed.WaitForProcessId);
    }
}

/// <summary>Sahte bir GitHub: yol → yanıt.</summary>
internal sealed class FakeReleaseServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = [];

    public FakeReleaseServer Route(string url, HttpStatusCode status, byte[] body)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    public FakeReleaseServer Route(string url, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Route(url, status, Encoding.UTF8.GetBytes(body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);

        if (!request.Headers.UserAgent.Any())
        {
            // GitHub API'si User-Agent'sız istekleri reddediyor.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }

        var response = _routes.TryGetValue(url, out var make)
            ? make()
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

public sealed class UpdateCheckerTests
{
    internal const string Latest = "https://api.github.com/repos/koronerap/SteamPipeManager/releases/latest";

    internal static string ReleaseJson(string tag, params string[] assets)
    {
        var list = string.Join(",", assets.Select(name =>
            $$"""{"name":"{{name}}","size":123,"browser_download_url":"https://github.com/koronerap/SteamPipeManager/releases/download/{{tag}}/{{name}}"}"""));

        return $$"""{"tag_name":"{{tag}}","name":"Release {{tag}}","html_url":"https://github.com/koronerap/SteamPipeManager/releases/tag/{{tag}}","assets":[{{list}}]}""";
    }

    private static UpdateChecker Checker(FakeReleaseServer server, ProductId product = ProductId.SteamPipeManager) =>
        new(new GitHubReleaseClient(new HttpClient(server), UpdateSource.Default), product);

    private static readonly AppVersion Current = new(1, 0, 1);

    [Fact]
    public async Task A_newer_release_with_this_products_package_and_checksums_is_installable()
    {
        var server = new FakeReleaseServer().Route(Latest, ReleaseJson("v1.0.2",
            "EpicBuildManager-win-x64.zip", "SteamPipeManager-win-x64.zip", "SHA256SUMS.txt"));

        var result = await Checker(server).CheckAsync(Current);

        Assert.Equal(UpdateAvailability.Available, result.Availability);
        Assert.Equal(new AppVersion(1, 0, 2), result.Release!.Version);
        Assert.Equal("SteamPipeManager-win-x64.zip", result.Package!.Name);
        Assert.Equal("SHA256SUMS.txt", result.Checksums!.Name);
    }

    [Fact]
    public async Task Each_product_looks_only_for_its_own_package()
    {
        var server = new FakeReleaseServer().Route(Latest, ReleaseJson("v1.0.2",
            "SteamPipeManager-win-x64.zip", "SHA256SUMS.txt"));

        var result = await Checker(server, ProductId.PipeManagerHub).CheckAsync(Current);

        Assert.Equal(UpdateAvailability.ManualOnly, result.Availability);
        Assert.Null(result.Package);
    }

    [Theory]
    [InlineData("v1.0.1")]
    [InlineData("v1.0.0")]
    public async Task The_same_or_an_older_release_is_up_to_date(string tag)
    {
        var server = new FakeReleaseServer().Route(Latest, ReleaseJson(tag,
            "SteamPipeManager-win-x64.zip", "SHA256SUMS.txt"));

        Assert.Equal(UpdateAvailability.UpToDate, (await Checker(server).CheckAsync(Current)).Availability);
    }

    [Fact]
    public async Task A_release_without_checksums_is_never_offered_for_in_app_install()
    {
        // v1.0.1 böyle yayımlandı: yalnızca exe, zip ve sağlama yok.
        var server = new FakeReleaseServer().Route(Latest, ReleaseJson("v1.0.2",
            "SteamPipeManager.exe", "SteamPipeManager-win-x64.zip"));

        var result = await Checker(server).CheckAsync(Current);

        Assert.Equal(UpdateAvailability.ManualOnly, result.Availability);
        Assert.Equal("https://github.com/koronerap/SteamPipeManager/releases/tag/v1.0.2", result.Release!.PageUrl!.ToString());
    }

    [Fact]
    public async Task Plain_http_download_links_are_not_trusted()
    {
        var json = ReleaseJson("v1.0.2", "SteamPipeManager-win-x64.zip", "SHA256SUMS.txt")
            .Replace("https://github.com/koronerap/SteamPipeManager/releases/download", "http://evil.example.com");

        var server = new FakeReleaseServer().Route(Latest, json);

        Assert.Equal(UpdateAvailability.ManualOnly, (await Checker(server).CheckAsync(Current)).Availability);
    }

    [Fact]
    public async Task No_releases_at_all_is_up_to_date()
    {
        Assert.Equal(UpdateAvailability.UpToDate, (await Checker(new FakeReleaseServer()).CheckAsync(Current)).Availability);
    }

    [Fact]
    public async Task Rate_limiting_is_reported_as_such()
    {
        var server = new FakeReleaseServer().Route(Latest, "{}", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Checker(server).CheckAsync(Current));
        Assert.Equal(UpdateFailure.RateLimited, ex.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("[]")]
    public async Task A_garbled_response_is_reported_not_crashed_on(string body)
    {
        var server = new FakeReleaseServer().Route(Latest, body);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Checker(server).CheckAsync(Current));
        Assert.Equal(UpdateFailure.InvalidResponse, ex.Failure);
    }
}

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spm-update-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string Base = "https://github.com/koronerap/SteamPipeManager/releases/download/v1.0.2/";

    private static async Task<(FakeReleaseServer Server, UpdateCheckResult Update, UpdateDownloader Downloader)> Arrange(
        byte[] package, string checksumText)
    {
        var server = new FakeReleaseServer()
            .Route(UpdateCheckerTests.Latest, UpdateCheckerTests.ReleaseJson("v1.0.2", "SteamPipeManager-win-x64.zip", "SHA256SUMS.txt"))
            .Route(Base + "SteamPipeManager-win-x64.zip", HttpStatusCode.OK, package)
            .Route(Base + "SHA256SUMS.txt", checksumText);

        var http = new HttpClient(server);
        var client = new GitHubReleaseClient(http, UpdateSource.Default);
        var update = await new UpdateChecker(client, ProductId.SteamPipeManager).CheckAsync(new AppVersion(1, 0, 1));

        return (server, update, new UpdateDownloader(http, client));
    }

    [Fact]
    public async Task A_package_matching_its_checksum_is_kept()
    {
        var package = Encoding.UTF8.GetBytes(new string('x', 250_000));
        var hash = Convert.ToHexStringLower(SHA256.HashData(package));
        var (_, update, downloader) = await Arrange(package, ChecksumFile.Format(hash, "SteamPipeManager-win-x64.zip"));

        var reports = new List<double>();
        var path = await downloader.DownloadAsync(update, _dir, new SyncProgress(reports.Add));

        Assert.Equal(package, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reports[^1]);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_package_that_does_not_match_is_deleted_and_reported()
    {
        var package = Encoding.UTF8.GetBytes("tampered");
        var wrong = new string('0', 64);
        var (_, update, downloader) = await Arrange(package, ChecksumFile.Format(wrong, "SteamPipeManager-win-x64.zip"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(update, _dir));

        Assert.Equal(UpdateFailure.ChecksumMismatch, ex.Failure);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_package_missing_from_the_checksum_list_is_not_even_downloaded()
    {
        var (server, update, downloader) = await Arrange(
            [1, 2, 3], ChecksumFile.Format(new string('a', 64), "PipeManagerHub-win-x64.zip"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(update, _dir));

        Assert.Equal(UpdateFailure.ChecksumMissing, ex.Failure);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith(".zip", StringComparison.Ordinal));
    }

    /// <summary><see cref="Progress{T}"/> bağlama göre sonradan raporluyor; testte senkron olmalı.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spm-update-inst-" + Guid.NewGuid().ToString("N"));

    private string AppDir => Path.Combine(_root, "app");

    private string Staging => Path.Combine(_root, "staging");

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(AppDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return path;
    }

    private void Existing(string name, string content) =>
        File.WriteAllText(Path.Combine(AppDir, name), content);

    private string Read(string name) => File.ReadAllText(Path.Combine(AppDir, name));

    [Fact]
    public void Stages_the_product_folder_that_publish_ps1_zips()
    {
        // Compress-Archive klasörün kendisini de zip'e koyuyor; Windows PowerShell 5.1
        // ayırıcı olarak ters eğik çizgi kullanıyor.
        var zip = MakeZip(("SteamPipeManager\\SteamPipeManager.exe", "new exe"), ("SteamPipeManager\\wpfgfx_cor3.dll", "new dll"));

        var staged = UpdateInstaller.Stage(zip, Staging, "SteamPipeManager.exe");

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(staged, "SteamPipeManager.exe")));
    }

    [Fact]
    public void Stages_a_package_whose_files_sit_at_the_root()
    {
        var zip = MakeZip(("SteamPipeManager.exe", "new exe"));

        Assert.Equal(Path.GetFullPath(Staging), UpdateInstaller.Stage(zip, Staging, "SteamPipeManager.exe"));
    }

    [Fact]
    public void Refuses_another_products_package()
    {
        var zip = MakeZip(("EpicBuildManager/EpicBuildManager.exe", "epic"));

        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(zip, Staging, "SteamPipeManager.exe"));
        Assert.Equal(UpdateFailure.PackageInvalid, ex.Failure);
    }

    [Fact]
    public void Refuses_entries_that_escape_the_staging_folder()
    {
        var zip = MakeZip(("SteamPipeManager.exe", "exe"), ("../../evil.dll", "evil"));

        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(zip, Staging, "SteamPipeManager.exe"));

        Assert.Equal(UpdateFailure.PackageInvalid, ex.Failure);
        Assert.False(File.Exists(Path.Combine(_root, "evil.dll")));
    }

    [Fact]
    public void Refuses_a_corrupt_zip()
    {
        var path = Path.Combine(_root, "corrupt.zip");
        File.WriteAllText(path, "this is not a zip");

        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(path, Staging, "SteamPipeManager.exe"));
        Assert.Equal(UpdateFailure.PackageInvalid, ex.Failure);
    }

    [Fact]
    public void Replaces_files_and_keeps_the_old_ones_aside_until_cleanup()
    {
        Existing("SteamPipeManager.exe", "old exe");
        Existing("wpfgfx_cor3.dll", "old dll");
        Existing("user-notes.txt", "not ours");

        var staged = UpdateInstaller.Stage(
            MakeZip(("SteamPipeManager/SteamPipeManager.exe", "new exe"),
                    ("SteamPipeManager/wpfgfx_cor3.dll", "new dll"),
                    ("SteamPipeManager/D3DCompiler_47_cor3.dll", "added")),
            Staging, "SteamPipeManager.exe");

        var installed = UpdateInstaller.Install(staged, AppDir);

        Assert.Equal(3, installed.FileCount);
        Assert.Equal("new exe", Read("SteamPipeManager.exe"));
        Assert.Equal("new dll", Read("wpfgfx_cor3.dll"));
        Assert.Equal("added", Read("D3DCompiler_47_cor3.dll"));
        Assert.Equal("not ours", Read("user-notes.txt"));
        Assert.Equal("old exe", Read("SteamPipeManager.exe" + UpdateInstaller.OldSuffix));

        Assert.Equal(0, UpdateInstaller.CleanupLeftovers(AppDir));
        Assert.Equal(
            ["D3DCompiler_47_cor3.dll", "SteamPipeManager.exe", "user-notes.txt", "wpfgfx_cor3.dll"],
            Directory.GetFiles(AppDir).Select(Path.GetFileName).Order());
    }

    [WindowsFact("açık dosya taşınamıyor")]
    public void A_file_that_cannot_be_moved_rolls_everything_back()
    {
        Existing("A.dll", "old a");
        Existing("B.dll", "old b");
        Existing("SteamPipeManager.exe", "old exe");

        var staged = UpdateInstaller.Stage(
            MakeZip(("A.dll", "new a"), ("B.dll", "new b"), ("SteamPipeManager.exe", "new exe")),
            Staging, "SteamPipeManager.exe");

        // Başka bir sürecin silme paylaşımı vermeden açık tuttuğu dosya: taşınamaz.
        using (new FileStream(Path.Combine(AppDir, "B.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, AppDir));
            Assert.Equal(UpdateFailure.InstallFailed, ex.Failure);
        }

        Assert.Equal("old a", Read("A.dll"));
        Assert.Equal("old b", Read("B.dll"));
        Assert.Equal("old exe", Read("SteamPipeManager.exe"));
        Assert.Equal(3, Directory.GetFiles(AppDir).Length);
    }

    [Fact]
    public void An_installed_update_can_be_rolled_back_when_the_new_version_will_not_start()
    {
        Existing("SteamPipeManager.exe", "old exe");

        var staged = UpdateInstaller.Stage(
            MakeZip(("SteamPipeManager.exe", "new exe"), ("New.dll", "new")),
            Staging, "SteamPipeManager.exe");

        var installed = UpdateInstaller.Install(staged, AppDir);

        Assert.True(installed.Rollback());
        Assert.Equal("old exe", Read("SteamPipeManager.exe"));
        Assert.Equal(["SteamPipeManager.exe"], Directory.GetFiles(AppDir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_locked_leftover_from_an_earlier_update_does_not_block_the_next_one()
    {
        Existing("SteamPipeManager.exe", "current exe");
        Existing("SteamPipeManager.exe" + UpdateInstaller.OldSuffix, "ancient exe");

        var staged = UpdateInstaller.Stage(MakeZip(("SteamPipeManager.exe", "new exe")), Staging, "SteamPipeManager.exe");

        // Silinemeyen ama adı değiştirilebilen artık.
        using (new FileStream(Path.Combine(AppDir, "SteamPipeManager.exe" + UpdateInstaller.OldSuffix),
                   FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateInstaller.Install(staged, AppDir);
        }

        Assert.Equal("new exe", Read("SteamPipeManager.exe"));
        Assert.Equal(0, UpdateInstaller.CleanupLeftovers(AppDir));
        Assert.Single(Directory.GetFiles(AppDir));
    }

    [WindowsFact("açık dosya silinemiyor")]
    public void Leftovers_still_in_use_are_left_for_the_next_start()
    {
        Existing("SteamPipeManager.exe" + UpdateInstaller.OldSuffix, "old");

        using (new FileStream(Path.Combine(AppDir, "SteamPipeManager.exe" + UpdateInstaller.OldSuffix),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(1, UpdateInstaller.CleanupLeftovers(AppDir));
        }

        Assert.Equal(0, UpdateInstaller.CleanupLeftovers(AppDir));
    }

    [Fact]
    public void A_development_build_is_not_updated_in_place()
    {
        Assert.Equal(
            InstallReadiness.DevelopmentBuild,
            UpdateInstaller.CheckReadiness(Path.Combine(AppDir, "SteamPipeManager.App.exe"), "SteamPipeManager.exe"));

        Assert.Equal(InstallReadiness.DevelopmentBuild, UpdateInstaller.CheckReadiness(null, "SteamPipeManager.exe"));
    }

    [Fact]
    public void A_release_copy_in_a_writable_folder_is_ready()
    {
        Assert.Equal(
            InstallReadiness.Ready,
            UpdateInstaller.CheckReadiness(Path.Combine(AppDir, "SteamPipeManager.exe"), "SteamPipeManager.exe"));

        Assert.Empty(Directory.GetFiles(AppDir));
    }
}
