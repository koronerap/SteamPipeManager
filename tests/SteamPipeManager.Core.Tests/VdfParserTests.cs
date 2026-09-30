using SteamPipeManager.Core.Vdf;

namespace SteamPipeManager.Core.Tests;

public class VdfParserTests
{
    [FixtureFact(Fixtures.ContentBuilder)]
    public void Parses_real_app_script()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("app_1200000.vdf"));

        Assert.Equal("appbuild", root.Key);
        Assert.Equal(1200000u, root.UIntOf("appid"));
        Assert.Equal("fix-update", root.ValueOf("desc"));
        Assert.Equal("debug", root.ValueOf("setlive"));
        Assert.False(root.BoolOf("preview"));
        Assert.Equal("", root.ValueOf("contentroot"));

        var depots = root.Child("depots");
        Assert.NotNull(depots);
        Assert.Equal(3, depots.Children.Count);
        Assert.Equal(
            ["1200002", "1200003", "1200001"],
            depots.Children.Select(d => d.Key));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Keeps_windows_paths_literal()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("depot_1200002.vdf"));

        // Ters bölü kaçışı işlenseydi "\t" sekmeye dönüşür ve yol bozulurdu.
        Assert.Equal(
            @"D:\sdk\tools\ContentBuilder\content\Skyward_Win",
            root.ValueOf("contentroot"));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Reads_depot_build_config_root_key()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("depot_1200001.vdf"));

        Assert.Equal("DepotBuildConfig", root.Key);
        Assert.Equal(1200001u, root.UIntOf("DepotID"));
        Assert.Equal("*.pdb", root.ValueOf("FileExclusion"));

        var mapping = root.Child("FileMapping");
        Assert.NotNull(mapping);
        Assert.Equal("*", mapping.ValueOf("LocalPath"));
        Assert.Equal(".", mapping.ValueOf("DepotPath"));
        Assert.True(mapping.BoolOf("recursive"));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Ignores_line_comments_in_sdk_sample()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("app_build_1000.vdf"));

        Assert.Equal("AppBuild", root.Key);
        Assert.Equal(1000u, root.UIntOf("AppID"));
        Assert.Equal("Sample build description", root.ValueOf("Desc"));
        Assert.Equal(2, root.Child("Depots")!.Children.Count);
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Key_lookup_is_case_insensitive()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("app_build_1000.vdf"));

        // Gerçek dosyalar "appid", SDK örneği "AppID" yazıyor; ikisi de aynı okunmalı.
        Assert.Equal(root.ValueOf("AppID"), root.ValueOf("appid"));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Reads_depot_blocks_nested_inside_app_script()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("simple_app_build.vdf"));

        var depot = root.Child("Depots")!.Child("1001");
        Assert.NotNull(depot);
        Assert.True(depot.IsBlock);
        Assert.Equal("*", depot.Child("FileMapping")!.ValueOf("LocalPath"));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void Reads_multiple_mappings_and_exclusions()
    {
        var root = VdfParser.ParseSingleRootFile(RefScripts.Path("depot_build_1002.vdf"));

        Assert.Equal(3, root.ChildrenNamed("FileMapping").Count());
        Assert.Equal(3, root.ChildrenNamed("FileExclusion").Count());
        Assert.Equal(
            @"localization\german\german_installscript.vdf",
            root.ValueOf("InstallScript"));
        Assert.Equal("userconfig", root.Child("FileProperties")!.ValueOf("Attributes"));
    }

    [Theory]
    [InlineData("\"a\" \"1\"", "1")]
    [InlineData("a 1", "1")]
    [InlineData("\"a\" 1", "1")]
    [InlineData("a \"1\"", "1")]
    public void Accepts_quoted_and_bare_tokens(string text, string expected)
    {
        Assert.Equal(expected, VdfParser.Parse(text).ValueOf("a"));
    }

    [Fact]
    public void Rejects_unclosed_block()
    {
        var ex = Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\"\r\n{\r\n\"b\" \"1\"\r\n"));
        Assert.Contains("not closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_key_without_value()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\" \"1\"\r\n\"b\""));
    }

    [Fact]
    public void Rejects_stray_closing_brace()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\" \"1\"\r\n}"));
    }

    [FixtureFact(Fixtures.ContentBuilder)]
    public void All_reference_scripts_parse()
    {
        foreach (var file in Directory.EnumerateFiles(RefScripts.Directory, "*.vdf"))
        {
            var ex = Record.Exception(() => VdfParser.ParseSingleRootFile(file));
            Assert.True(ex is null, $"{Path.GetFileName(file)} ayrıştırılamadı: {ex?.Message}");
        }
    }
}
