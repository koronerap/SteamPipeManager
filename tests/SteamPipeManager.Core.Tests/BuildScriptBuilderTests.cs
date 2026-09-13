using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Vdf;

namespace SteamPipeManager.Core.Tests;

public class BuildScriptBuilderTests
{
    private const string SdkRoot = @"D:\sdk\tools\ContentBuilder";

    /// <summary>app_1200000.vdf (Skyward) referans script'inin model karşılığı.</summary>
    private static SubApp Skyward() => new()
    {
        Title = "Skyward",
        Kind = SubAppKind.Main,
        SteamAppId = 1200000,
        ContentRoot = null,
        SetLiveBranch = "debug",
        Depots =
        [
            new DepotConfig
            {
                DepotId = 1200002,
                Label = "Windows",
                ContentRoot = $@"{SdkRoot}\content\Skyward_Win",
                FileExclusions = ["*.pdb"],
            },
            new DepotConfig
            {
                DepotId = 1200003,
                Label = "macOS",
                ContentRoot = $@"{SdkRoot}\content\Skyward_Mac",
                FileExclusions = ["*.pdb"],
            },
            new DepotConfig
            {
                DepotId = 1200001,
                Label = "Linux",
                ContentRoot = $@"{SdkRoot}\content\Skyward_Linux",
                FileExclusions = ["*.pdb"],
            },
        ],
    };

    private static Dictionary<uint, string> ScriptPaths(SubApp subApp) =>
        subApp.Depots.ToDictionary(d => d.DepotId, d => $@"{SdkRoot}\scripts\depot_{d.DepotId}.vdf");

    [Fact]
    public void Depot_script_matches_reference_semantics()
    {
        var subApp = Skyward();
        var depot = subApp.Depots.Single(d => d.DepotId == 1200002);

        var generated = BuildScriptBuilder.BuildDepotScript(depot, subApp);
        var reference = VdfParser.ParseSingleRootFile(RefScripts.Path("depot_1200002.vdf"));

        VdfWriterTests.AssertTreesEqual(reference, generated, "depot_1200002.vdf");
    }

    [Fact]
    public void Every_reference_depot_script_can_be_reproduced()
    {
        var subApp = Skyward();

        foreach (var depot in subApp.Depots)
        {
            var fileName = $"depot_{depot.DepotId}.vdf";
            var generated = BuildScriptBuilder.BuildDepotScript(depot, subApp);
            var reference = VdfParser.ParseSingleRootFile(RefScripts.Path(fileName));

            VdfWriterTests.AssertTreesEqual(reference, generated, fileName);
        }
    }

    [Fact]
    public void App_script_matches_reference_semantics()
    {
        var subApp = Skyward();

        var generated = BuildScriptBuilder.BuildAppScript(
            subApp,
            buildOutputDirectory: $@"{SdkRoot}\output",
            depotScriptPaths: ScriptPaths(subApp),
            description: "fix-update");

        var reference = VdfParser.ParseSingleRootFile(RefScripts.Path("app_1200000.vdf"));

        VdfWriterTests.AssertTreesEqual(reference, generated, "app_1200000.vdf");
    }

    [Fact]
    public void App_script_writes_empty_contentroot_when_depots_carry_their_own()
    {
        var generated = BuildScriptBuilder.BuildAppScript(
            Skyward(), $@"{SdkRoot}\output", ScriptPaths(Skyward()), "x");

        Assert.Equal("", generated.ValueOf("contentroot"));
    }

    [Fact]
    public void Trailing_separator_is_stripped_from_directories()
    {
        var subApp = Skyward();

        var generated = BuildScriptBuilder.BuildAppScript(
            subApp, $@"{SdkRoot}\output\", ScriptPaths(subApp), "x");

        Assert.Equal($@"{SdkRoot}\output", generated.ValueOf("buildoutput"));
    }

    [Fact]
    public void Drive_root_keeps_its_separator()
    {
        var subApp = Skyward();

        var generated = BuildScriptBuilder.BuildAppScript(subApp, @"D:\", ScriptPaths(subApp), "x");

        Assert.Equal(@"D:\", generated.ValueOf("buildoutput"));
    }

    [Fact]
    public void Preview_override_forces_preview_flag()
    {
        var subApp = Skyward();
        Assert.False(subApp.Preview);

        var generated = BuildScriptBuilder.BuildAppScript(
            subApp, $@"{SdkRoot}\output", ScriptPaths(subApp), "x", previewOverride: true);

        Assert.Equal("1", generated.ValueOf("preview"));
    }

    [Fact]
    public void Depot_falls_back_to_subapp_content_root()
    {
        var subApp = new SubApp
        {
            SteamAppId = 1300020,
            ContentRoot = @"D:\Builds\Demo",
            Depots = [new DepotConfig { DepotId = 1300021 }],
        };

        var generated = BuildScriptBuilder.BuildDepotScript(subApp.Depots[0], subApp);

        Assert.Equal(@"D:\Builds\Demo", generated.ValueOf("contentroot"));
    }

    [Fact]
    public void Depot_content_root_wins_over_subapp()
    {
        var subApp = new SubApp
        {
            SteamAppId = 1,
            ContentRoot = @"D:\Fallback",
            Depots = [new DepotConfig { DepotId = 2, ContentRoot = @"D:\Specific" }],
        };

        var generated = BuildScriptBuilder.BuildDepotScript(subApp.Depots[0], subApp);

        Assert.Equal(@"D:\Specific", generated.ValueOf("contentroot"));
    }

    [Fact]
    public void Missing_content_root_is_rejected()
    {
        var subApp = new SubApp { SteamAppId = 1, Depots = [new DepotConfig { DepotId = 2 }] };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildScriptBuilder.BuildDepotScript(subApp.Depots[0], subApp));

        Assert.Contains("No content folder", ex.Message);
    }

    [Fact]
    public void Missing_depot_script_path_is_rejected()
    {
        var subApp = Skyward();

        Assert.Throws<InvalidOperationException>(
            () => BuildScriptBuilder.BuildAppScript(
                subApp, $@"{SdkRoot}\output", new Dictionary<uint, string>(), "x"));
    }

    [Fact]
    public void Multiple_mappings_and_exclusions_are_written_in_order()
    {
        var subApp = new SubApp
        {
            SteamAppId = 1,
            Depots =
            [
                new DepotConfig
                {
                    DepotId = 1002,
                    ContentRoot = @"D:\content\depot1002",
                    FileMappings =
                    [
                        new FileMapping { LocalPath = @"bin\*", DepotPath = @"executables\", Recursive = true },
                        new FileMapping { LocalPath = @"localization\german\audio\*", DepotPath = @"audio\", Recursive = false },
                    ],
                    FileExclusions = [@"bin\server.exe", "*.pdb"],
                },
            ],
        };

        var generated = BuildScriptBuilder.BuildDepotScript(subApp.Depots[0], subApp);

        Assert.Equal(2, generated.ChildrenNamed("FileMapping").Count());
        Assert.Equal(
            [@"bin\server.exe", "*.pdb"],
            generated.ChildrenNamed("FileExclusion").Select(n => n.Value));
        Assert.Equal("0", generated.ChildrenNamed("FileMapping").Last().ValueOf("recursive"));
    }
}
