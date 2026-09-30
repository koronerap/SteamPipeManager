using System.Text;
using SteamPipeManager.Core.Vdf;

namespace SteamPipeManager.Core.Tests;

public class VdfWriterTests
{
    [Fact]
    public void Uses_crlf_tabs_and_no_trailing_newline()
    {
        var root = VdfNode.Block("appbuild");
        root.Add("appid", "1000");
        root.Add(VdfNode.Block("depots").Add("1001", "depot_1001.vdf"));

        var text = VdfWriter.Write(root);

        Assert.Equal(
            "\"appbuild\"\r\n{\r\n\t\"appid\" \"1000\"\r\n\t\"depots\"\r\n\t{\r\n\t\t\"1001\" \"depot_1001.vdf\"\r\n\t}\r\n}",
            text);
        Assert.DoesNotContain('\n', text.TrimEnd('\r', '\n').Replace("\r\n", ""));
        Assert.EndsWith("}", text);
    }

    [Fact]
    public void Writes_empty_block_without_blank_line()
    {
        var text = VdfWriter.Write(VdfNode.Block("depots"));

        Assert.Equal("\"depots\"\r\n{\r\n}", text);
    }

    [Fact]
    public void Writes_windows_paths_without_escaping()
    {
        var text = VdfWriter.Write(VdfNode.Leaf("contentroot", @"D:\sdk\tools\ContentBuilder"));

        Assert.Equal("\"contentroot\" \"D:\\sdk\\tools\\ContentBuilder\"", text);
    }

    [Theory]
    [InlineData("quote\"inside")]
    [InlineData("line\nbreak")]
    public void Rejects_values_that_would_corrupt_the_file(string value)
    {
        Assert.Throws<ArgumentException>(() => VdfWriter.Write(VdfNode.Leaf("desc", value)));
    }

    [Fact]
    public async Task File_is_written_without_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spm_{Guid.NewGuid():N}.vdf");

        try
        {
            await VdfWriter.WriteFileAsync(path, VdfNode.Leaf("appid", "1000"));
            var bytes = File.ReadAllBytes(path);

            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "Dosya BOM ile başlamamalı.");
            Assert.Equal("\"appid\" \"1000\"", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Referans dosyalar ayrıştırılıp yeniden yazıldığında anlamları korunmalı.
    /// Bayt düzeyinde eşitlik aranmaz: kaynak dosyalar tutarsız boşluk kullanıyor
    /// (<c>"appid" "…"</c> boşluklu ama <c>"local"</c> sekmeli), üretilen çıktı tutarlıdır.
    /// </summary>
    [FixtureFact(Fixtures.ContentBuilder)]
    public void Round_trip_preserves_every_reference_script()
    {
        foreach (var file in Directory.EnumerateFiles(RefScripts.Directory, "*.vdf"))
        {
            var original = VdfParser.ParseSingleRootFile(file);
            var reparsed = VdfParser.ParseSingleRoot(VdfWriter.Write(original));

            AssertTreesEqual(original, reparsed, Path.GetFileName(file));
        }
    }

    internal static void AssertTreesEqual(VdfNode expected, VdfNode actual, string context)
    {
        Assert.True(
            string.Equals(expected.Key, actual.Key, StringComparison.OrdinalIgnoreCase),
            $"{context}: anahtar farklı ({expected.Key} != {actual.Key})");
        Assert.True(expected.IsBlock == actual.IsBlock, $"{context}/{expected.Key}: düğüm tipi farklı");
        Assert.True(expected.Value == actual.Value, $"{context}/{expected.Key}: değer farklı");
        Assert.True(
            expected.Children.Count == actual.Children.Count,
            $"{context}/{expected.Key}: alt düğüm sayısı farklı");

        for (var i = 0; i < expected.Children.Count; i++)
        {
            AssertTreesEqual(expected.Children[i], actual.Children[i], $"{context}/{expected.Key}");
        }
    }
}
