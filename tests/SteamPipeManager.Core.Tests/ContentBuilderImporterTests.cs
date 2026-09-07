using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Vdf;

namespace SteamPipeManager.Core.Tests;

public class ContentBuilderImporterTests
{
    [Fact]
    public void Imports_every_real_app_script_from_ref_scripts()
    {
        var result = ContentBuilderImporter.ImportFrom(RefScripts.Directory);

        // ref_scripts dört gerçek app script'i ve iki SDK örneği içeriyor; hepsi appbuild kökü.
        var appIds = result.SubApps.Select(s => s.SubApp.SteamAppId).ToHashSet();

        Assert.Contains(1200000u, appIds);
        Assert.Contains(1300000u, appIds);
        Assert.Contains(1300010u, appIds);
        Assert.Contains(1300020u, appIds);
    }

    [Fact]
    public void Imports_skyward_with_three_platform_depots()
    {
        var skyward = Import(1200000);

        Assert.Equal("debug", skyward.SetLiveBranch);
        Assert.Null(skyward.ContentRoot);
        Assert.Equal(3, skyward.Depots.Count);

        Assert.Equal(
            ["Windows", "macOS", "Linux"],
            skyward.Depots.Select(d => d.Label));

        var windows = skyward.Depots.Single(d => d.DepotId == 1200002);
        Assert.Equal(@"D:\sdk\tools\ContentBuilder\content\Skyward_Win", windows.ContentRoot);
        Assert.Equal(["*.pdb"], windows.FileExclusions);
        Assert.Single(windows.FileMappings);
        Assert.True(windows.FileMappings[0].Recursive);
    }

    [Fact]
    public void Resolves_depot_scripts_by_filename_when_absolute_path_is_stale()
    {
        // App script'leri D:\sdk\... mutlak yolunu gösteriyor; o klasör bu makinede yok,
        // importer dosya adına düşerek ref_scripts içinden çözmeli.
        var skyward = Import(1200000);

        Assert.All(skyward.Depots, d => Assert.NotNull(d.ContentRoot));
    }

    /// <summary>
    /// SDK örnekleri her ContentBuilder kopyasında bulunur ve hepsi AppID 1000 kullanır;
    /// ikisi birden aktarılırsa "aynı AppID iki kez" doğrulama hatası çıkar.
    /// Gerçek bir kurulum tarandığında bu ortaya çıktı.
    /// </summary>
    [Fact]
    public void Marks_sdk_sample_scripts()
    {
        var result = ContentBuilderImporter.ImportFrom(RefScripts.Directory);

        var samples = result.SubApps.Where(s => s.IsSdkSample).Select(s => Path.GetFileName(s.SourceAppScript));
        Assert.Equal(["app_build_1000.vdf", "simple_app_build.vdf"], samples.Order());

        // Gerçek oyun script'leri örnek olarak işaretlenmemeli.
        Assert.All(
            result.SubApps.Where(s => s.SubApp.SteamAppId != 1000),
            s => Assert.False(s.IsSdkSample));
    }

    [Fact]
    public void Sdk_samples_carry_an_explaining_warning()
    {
        var sample = ContentBuilderImporter.ImportFrom(RefScripts.Directory).SubApps
            .Single(s => s.SourceAppScript.EndsWith("simple_app_build.vdf", StringComparison.Ordinal));

        // Dil paketleri testte yüklenmediği için Loc anahtarın kendisini döndürür;
        // anahtar zaten mesajın kalıcı kimliği.
        Assert.Contains(sample.Warnings, w => w.Contains("Import.SdkSample"));
    }

    /// <summary>
    /// desc alanı build notudur ("fix-update", "0.1.6 beta"); başlık olarak kullanılırsa
    /// oyun listesi anlamsız görünür. Başlık türden türetilir, not açıklamaya gider.
    /// </summary>
    [Fact]
    public void Title_comes_from_kind_not_build_description()
    {
        var beta = Import(1300010);

        // Başlık artık dil paketinden geliyor; testte paket yüklü olmadığı için
        // Loc anahtarı döndürüyor — kontrol edilen şey zaten "tür kullanılıyor mu".
        Assert.Equal("Kind.Beta", beta.Title);
        Assert.Equal("0.4.2 beta", beta.BuildDescriptionTemplate);

        Assert.Equal("Kind.Main", Import(1200000).Title);
        Assert.Equal("fix-update", Import(1200000).BuildDescriptionTemplate);
    }

    [Fact]
    public void Detects_demo_from_description()
    {
        Assert.Equal(SubAppKind.Demo, Import(1300020).Kind);
    }

    [Fact]
    public void Detects_beta_from_description()
    {
        Assert.Equal(SubAppKind.Beta, Import(1300010).Kind);
    }

    [Theory]
    [InlineData("demo-init", "app_1.vdf", SubAppKind.Demo)]
    [InlineData("0.1.6 beta", "app_1.vdf", SubAppKind.Beta)]
    [InlineData("internal playtest", "app_1.vdf", SubAppKind.Playtest)]
    [InlineData("fix-update", "app_1.vdf", SubAppKind.Main)]
    [InlineData("", "app_demo_1.vdf", SubAppKind.Demo)]
    public void Kind_guessing_uses_description_and_filename(string desc, string file, SubAppKind expected)
    {
        Assert.Equal(expected, ContentBuilderImporter.GuessKind(desc, file));
    }

    [Theory]
    [InlineData(@"D:\a\content\Skyward_Win", "Windows")]
    [InlineData(@"D:\a\content\Skyward_Mac", "macOS")]
    [InlineData(@"D:\a\content\Skyward_Linux", "Linux")]
    [InlineData(@"D:\a\content\Windows\", "Windows")]
    [InlineData(@"D:\a\content\Assets", "Assets")]
    [InlineData(null, "")]
    public void Label_guessing_reads_platform_from_folder_name(string? contentRoot, string expected)
    {
        Assert.Equal(expected, ContentBuilderImporter.GuessLabel(contentRoot));
    }

    [Fact]
    public void Imports_depot_block_embedded_in_app_script()
    {
        var simple = ContentBuilderImporter.ImportFrom(RefScripts.Directory).SubApps
            .Single(s => s.SourceAppScript.EndsWith("simple_app_build.vdf", StringComparison.Ordinal));

        var depot = Assert.Single(simple.SubApp.Depots);
        Assert.Equal(1001u, depot.DepotId);
        Assert.Single(depot.FileMappings);
        Assert.Equal("*", depot.FileMappings[0].LocalPath);
    }

    [Fact]
    public void Reports_missing_depot_script_as_warning_without_failing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spm_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(
                Path.Combine(dir, "app_777.vdf"),
                "\"appbuild\"\r\n{\r\n\t\"appid\" \"777\"\r\n\t\"depots\"\r\n\t{\r\n\t\t\"778\" \"depot_778.vdf\"\r\n\t}\r\n}");

            var imported = Assert.Single(ContentBuilderImporter.ImportFrom(dir).SubApps);

            Assert.Equal(777u, imported.SubApp.SteamAppId);
            Assert.Equal(778u, Assert.Single(imported.SubApp.Depots).DepotId);
            Assert.Contains(imported.Warnings, w => w.Contains("Import.DepotScriptMissing"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reports_directory_without_scripts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spm_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var result = ContentBuilderImporter.ImportFrom(dir);

            Assert.Empty(result.SubApps);
            Assert.NotEmpty(result.Warnings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locates_scripts_directory_from_sdk_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"spm_sdk_{Guid.NewGuid():N}");
        var scripts = Path.Combine(root, "tools", "ContentBuilder", "scripts");
        Directory.CreateDirectory(scripts);

        try
        {
            File.WriteAllText(Path.Combine(scripts, "app_1.vdf"), "\"appbuild\"\r\n{\r\n\t\"appid\" \"1\"\r\n}");

            Assert.Equal(scripts, ContentBuilderImporter.LocateScriptsDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>İçe aktarılan model geri yazıldığında kaynak script'le aynı anlama gelmeli.</summary>
    [Fact]
    public void Imported_model_regenerates_equivalent_scripts()
    {
        var skyward = Import(1200000);

        var paths = skyward.Depots.ToDictionary(
            d => d.DepotId,
            d => $@"D:\sdk\tools\ContentBuilder\scripts\depot_{d.DepotId}.vdf");

        var regenerated = BuildScriptBuilder.BuildAppScript(
            skyward, @"D:\sdk\tools\ContentBuilder\output", paths, "fix-update");

        var original = VdfParser.ParseSingleRootFile(RefScripts.Path("app_1200000.vdf"));

        VdfWriterTests.AssertTreesEqual(original, regenerated, "app_1200000.vdf");
    }

    /// <summary>
    /// Gerçek bir kurulum tarandığında ortaya çıktı: Skyward'ın script'i Pixel Racer
    /// klasöründe duruyordu, bu yüzden script'in klasöründen ad türetmek yanlış sonuç verdi.
    /// İçerik klasörü çok daha güvenilir bir ipucu.
    /// </summary>
    [Fact]
    public void App_title_comes_from_content_folder_when_it_is_meaningful()
    {
        var skyward = ContentBuilderImporter.ImportFrom(RefScripts.Directory).SubApps
            .Single(s => s.SubApp.SteamAppId == 1200000);

        // …\content\Skyward_Win → "Skyward" (platform eki atılır)
        Assert.Equal("Skyward", ContentBuilderImporter.SuggestAppTitle(skyward));
    }

    [Fact]
    public void App_title_falls_back_to_installation_folder_for_plain_platform_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spm_title_{Guid.NewGuid():N}");
        var scripts = Path.Combine(dir, "SteamPipe (Pixel Racer)", "tools", "ContentBuilder", "scripts");
        var content = Path.Combine(dir, "SteamPipe (Pixel Racer)", "tools", "ContentBuilder", "content", "Windows");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(content);

        try
        {
            File.WriteAllText(
                Path.Combine(scripts, "app_1300000.vdf"),
                """
                "appbuild"
                {
                	"appid" "1300000"
                	"depots"
                	{
                		"1300001" "depot_1300001.vdf"
                	}
                }
                """);

            File.WriteAllText(
                Path.Combine(scripts, "depot_1300001.vdf"),
                $$"""
                  "DepotBuildConfig"
                  {
                  	"DepotID" "1300001"
                  	"contentroot" "{{content}}"
                  }
                  """);

            var imported = ContentBuilderImporter.ImportFrom(scripts).SubApps.Single();

            // İçerik klasörü sadece "Windows" — kurulum klasörünün parantez içi kullanılır.
            Assert.Equal("Pixel Racer", ContentBuilderImporter.SuggestAppTitle(imported));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static SubApp Import(uint appId) =>
        ContentBuilderImporter.ImportFrom(RefScripts.Directory).SubApps
            .Single(s => s.SubApp.SteamAppId == appId).SubApp;
}
