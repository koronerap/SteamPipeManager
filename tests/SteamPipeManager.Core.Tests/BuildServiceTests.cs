using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Vdf;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

[Collection("SteamCmd")]
public sealed class BuildServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_build_{Guid.NewGuid():N}");
    private readonly string _content;

    public BuildServiceTests()
    {
        _content = Path.Combine(_root, "content");
        Directory.CreateDirectory(_content);
        File.WriteAllText(Path.Combine(_content, "game.exe"), "oyun");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private WorkspaceLayout Layout => new(Path.Combine(_root, "workspace"));

    private BuildRequest Request(Action<SubApp>? tweak = null)
    {
        var subApp = new SubApp
        {
            Title = "Ana Oyun",
            SteamAppId = 1000,
            SetLiveBranch = "debug",
            Depots = [new DepotConfig { DepotId = 1001, Label = "Windows", ContentRoot = _content }],
        };

        tweak?.Invoke(subApp);

        var app = new SteamApp { Title = "Test Oyunu", SubApps = [subApp] };
        var profile = new UserProfile { DisplayName = "Test", SteamUsername = "tester", Apps = [app] };

        return new BuildRequest(profile, app, subApp, "test build");
    }

    private static CancellationToken Timeout(int seconds = 40) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [Fact]
    public async Task Writes_app_and_depot_scripts_to_workspace()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);
        var request = Request();

        var scriptPath = await service.WriteScriptsAsync(request);

        Assert.True(File.Exists(scriptPath));
        Assert.EndsWith("app_1000.vdf", scriptPath);

        var depotPath = Path.Combine(Path.GetDirectoryName(scriptPath)!, "depot_1001.vdf");
        Assert.True(File.Exists(depotPath));

        var appScript = VdfParser.ParseSingleRootFile(scriptPath);
        Assert.Equal("appbuild", appScript.Key);
        Assert.Equal(1000u, appScript.UIntOf("appid"));
        Assert.Equal("debug", appScript.ValueOf("setlive"));
        Assert.Equal("test build", appScript.ValueOf("desc"));

        // App script'i depot dosyasına mutlak yolla işaret etmeli.
        Assert.Equal(depotPath, appScript.Child("depots")!.ValueOf("1001"));

        var depotScript = VdfParser.ParseSingleRootFile(depotPath);
        Assert.Equal("DepotBuildConfig", depotScript.Key);
        Assert.Equal(_content, depotScript.ValueOf("contentroot"));
    }

    [Fact]
    public async Task Scripts_are_regenerated_on_each_write()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);
        var request = Request();

        var path = await service.WriteScriptsAsync(request);
        await File.WriteAllTextAsync(path, "elle bozuldu");

        await service.WriteScriptsAsync(request);

        Assert.Equal("appbuild", VdfParser.ParseSingleRootFile(path).Key);
    }

    [Fact]
    public async Task Preview_override_is_written_into_the_script()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);

        var request = Request() with { PreviewOverride = true };
        var path = await service.WriteScriptsAsync(request);

        Assert.Equal("1", VdfParser.ParseSingleRootFile(path).ValueOf("preview"));
    }

    [Fact]
    public async Task Successful_build_records_build_id_and_log()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);

        var result = await service.BuildAsync(Request(), ct: Timeout());

        Assert.True(result.Started);
        Assert.Equal(BuildOutcome.Succeeded, result.Record.Outcome);
        Assert.Equal(4242u, result.Record.SteamBuildId);
        Assert.NotNull(result.Record.LogFilePath);
        Assert.True(File.Exists(result.Record.LogFilePath!));
        Assert.NotNull(result.Record.Duration);
    }

    /// <summary>
    /// Boş içerik klasörüyle build almak Steam'de yayındaki içeriği siler;
    /// SteamCMD hiç başlatılmamalı.
    /// </summary>
    [Fact]
    public async Task Empty_content_directory_blocks_the_build()
    {
        var empty = Path.Combine(_root, "bos");
        Directory.CreateDirectory(empty);

        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);

        var result = await service.BuildAsync(
            Request(s => s.Depots[0].ContentRoot = empty), ct: Timeout());

        Assert.False(result.Started);
        Assert.Contains("Validate.ContentEmpty", result.BlockedReason);
        Assert.Equal(BuildOutcome.Failed, result.Record.Outcome);
    }

    [Fact]
    public async Task Default_branch_blocks_the_build()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new BuildService(Layout, fake.Installation);

        var result = await service.BuildAsync(
            Request(s => s.SetLiveBranch = "default"), ct: Timeout());

        Assert.False(result.Started);
        Assert.Contains("Validate.ForbiddenBranch", result.BlockedReason);
    }

    /// <summary>Oturum kontrolü geçilemezse SteamCMD build için hiç çalıştırılmaz.</summary>
    [Fact]
    public async Task Failed_session_check_blocks_the_build()
    {
        using var fake = FakeSteamCmd.Create("loginfail");
        var service = new BuildService(Layout, fake.Installation);

        var result = await service.BuildAsync(Request(), ct: Timeout());

        Assert.False(result.Started);
        Assert.Contains("Build.SessionCheckFailed", result.BlockedReason);
    }

    [Fact]
    public async Task Build_failure_records_reason()
    {
        using var fake = FakeSteamCmd.Create("buildfail");
        var service = new BuildService(Layout, fake.Installation);

        // Oturum kontrolü de aynı senaryoyu oynatır; giriş "OK" olduğu için build'e geçilir.
        var result = await service.BuildAsync(Request(), ct: Timeout());

        Assert.True(result.Started);
        Assert.Equal(BuildOutcome.Failed, result.Record.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Record.FailureReason));
    }

    [Fact]
    public async Task Only_one_build_runs_at_a_time()
    {
        using var fake = FakeSteamCmd.Create("stall");
        var service = new BuildService(Layout, fake.Installation) { StallTimeout = TimeSpan.FromSeconds(4) };

        var first = service.BuildAsync(Request(), ct: Timeout(60));

        // İlk build'in kilidi almasını bekle.
        while (!service.IsBuilding)
        {
            await Task.Delay(20, Timeout());
        }

        var second = await service.BuildAsync(Request(), ct: Timeout());

        Assert.False(second.Started);
        Assert.Contains("Build.AlreadyRunning", second.BlockedReason);

        await first;
    }
}
