using System.Runtime.CompilerServices;
using SteamPipeManager.Core.Epic;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// BuildPatchTool log ayrıştırıcısı. Satır biçimleri gerçek bir çalıştırmadan ölçüldü
/// (bkz. tools/BptProbe); testlerin bir kısmı o çalıştırmanın kaydına karşı koşuyor.
/// </summary>
public sealed class BptLogParserTests
{
    /// <summary>Gerçek bir <c>ChunkBuildDirectory</c> çalıştırmasının anonimleştirilmiş log'u.</summary>
    private static string RealLog([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "real_bpt_chunk_generation.log");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("real_bpt_chunk_generation.log bulunamadı.");
    }

    private static List<BptEvent> ParseRealLog(long? totalBytes = null)
    {
        var parser = new BptLogParser { TotalBytes = totalBytes };

        return [.. RealLog()
            .Split('\n')
            .Select(line => parser.Feed(line.TrimEnd('\r')))
            .OfType<BptEvent>()];
    }

    // --- Satır biçiminin ayrıştırılması ---

    [Fact]
    public void The_timestamp_and_category_are_read_from_the_line_prefix()
    {
        var parser = new BptLogParser();

        var evt = parser.Feed(
            "[2026.09.10-13.56.44:462][  0]LogBuildPatchTool: Display: Chunk generation complete.");

        Assert.NotNull(evt);
        Assert.Equal("LogBuildPatchTool", evt.Category);
        Assert.Equal(BptEventKind.Succeeded, evt.Kind);

        Assert.NotNull(evt.Timestamp);
        Assert.Equal(new DateTime(2026, 9, 10, 13, 56, 44, 462), evt.Timestamp.Value.DateTime);
    }

    /// <summary>
    /// Motor açılmadan önceki satırlarda köşeli parantezli önek yok; bunlar da
    /// düşürülmemeli, yoksa log panelinde açılış bilgileri kaybolur.
    /// </summary>
    [Fact]
    public void A_line_without_the_bracketed_prefix_is_still_an_event()
    {
        var evt = new BptLogParser().Feed(
            "LogWindows: File '../../../Engine/Binaries/ThirdParty/DbgHelp/dbghelp.dll' does not exist");

        Assert.NotNull(evt);
        Assert.Equal("LogWindows", evt.Category);
        Assert.Null(evt.Timestamp);
    }

    [Fact]
    public void Blank_lines_produce_nothing()
    {
        var parser = new BptLogParser();

        Assert.Null(parser.Feed(""));
        Assert.Null(parser.Feed("   "));
    }

    // --- Aşama işaretleri ---

    [Fact]
    public void The_start_line_carries_the_version_and_artifact()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.56.44:462][  0]LogBuildPatchTool: Display: Beginning chunk generation " +
            "of version 1.2.0 of artifact my-artifact. Build root: D:/builds/Windows");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.Started, evt.Kind);
        Assert.Equal("1.2.0", evt.BuildVersion);
        Assert.Equal("my-artifact", evt.ArtifactId);
    }

    [Fact]
    public void The_file_count_is_read()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.56.44:497][  2]LogPatchGeneration: Enumerated 8 files in 197 us");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.FilesEnumerated, evt.Kind);
        Assert.Equal(8, evt.FileCount);
    }

    /// <summary>
    /// Araç yüzde bildirmiyor, yalnızca taranan bayt konumunu. Yüzde toplam boyut
    /// bilindiğinde burada hesaplanıyor.
    /// </summary>
    [Fact]
    public void Scan_progress_becomes_a_percentage_when_the_total_size_is_known()
    {
        var parser = new BptLogParser { TotalBytes = 100_000_000 };

        var evt = parser.Feed(
            "[2026.09.10-13.56.44:721][  8]LogDataScanner: @25000000: Scanner completed in 0 us with 0 collisions.");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.ScanProgress, evt.Kind);
        Assert.Equal(25_000_000, evt.ByteOffset);
        Assert.Equal(25.0, evt.Percent);
    }

    [Fact]
    public void Scan_progress_without_a_known_total_still_reports_the_offset()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.56.44:721][  8]LogDataScanner: @33030145: Scanner completed in 0 us with 0 collisions.");

        Assert.NotNull(evt);
        Assert.Equal(33_030_145, evt.ByteOffset);
        Assert.Null(evt.Percent);
    }

    /// <summary>
    /// Satır başındaki ikinci alan motor tick sayacı. Sona doğru 100'e yaklaşıyor ve
    /// yüzde sanılmaya çok müsait; ilerleme oradan okunmamalı.
    /// </summary>
    [Fact]
    public void The_tick_counter_in_the_prefix_is_not_mistaken_for_progress()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.56.47:869][100]LogBuildPatchTool: Display: Chunk generation complete.");

        Assert.NotNull(evt);
        Assert.Null(evt.Percent);
        Assert.Equal(BptEventKind.Succeeded, evt.Kind);
    }

    // --- Başarısızlıklar ---

    [Fact]
    public void The_exit_reason_is_read_by_name_not_just_by_code()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.53.05:363][  0]LogBuildPatchTool: Error: Tool exited with MissingCredentials (9)");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.Failed, evt.Kind);
        Assert.Equal("MissingCredentials", evt.ExitReason);
        Assert.Equal(9, evt.ExitCode);
        Assert.True(evt.IsFailure);
    }

    [Theory]
    [InlineData("LogBPTOnline: Error: Missing credentials. Please ensure you have specified ClientId")]
    [InlineData("OSS: Client auth request failed. Sorry the client credentials you are using are invalid")]
    [InlineData("An authentication error occurred. Check you have specified ClientId")]
    public void Credential_problems_are_recognised(string line)
    {
        var evt = new BptLogParser().Feed(line);

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.AuthenticationFailed, evt.Kind);
    }

    [Fact]
    public void A_missing_required_argument_is_a_validation_failure()
    {
        var evt = new BptLogParser().Feed(
            "[2026.09.10-13.53.05:363][  0]LogBuildPatchTool: Error: ProductId is required for UploadBinary mode.");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.ValidationFailed, evt.Kind);
    }

    /// <summary>
    /// Sıralama önemli: çıkış satırı "MissingCredentials" kelimesini taşıyor ve kimlik
    /// hatası kontrolünden önce yakalanmalı, yoksa çıkış kodu kaybolur.
    /// </summary>
    [Fact]
    public void An_exit_line_naming_credentials_is_still_an_exit_line()
    {
        var evt = new BptLogParser().Feed("LogBuildPatchTool: Error: Tool exited with MissingCredentials (9)");

        Assert.NotNull(evt);
        Assert.Equal(BptEventKind.Failed, evt.Kind);
        Assert.Equal(9, evt.ExitCode);
    }

    // --- Gerçek çalıştırmanın kaydına karşı ---

    [FixtureFact(Fixtures.BptChunkLog)]
    public void The_real_run_is_recognised_from_start_to_finish()
    {
        var events = ParseRealLog();

        var started = Assert.Single(events, e => e.Kind == BptEventKind.Started);
        Assert.Equal("0.0.0-probe", started.BuildVersion);

        Assert.Equal(8, Assert.Single(events, e => e.Kind == BptEventKind.FilesEnumerated).FileCount);
        Assert.Single(events, e => e.Kind == BptEventKind.ManifestSaved);
        Assert.Single(events, e => e.Kind == BptEventKind.Succeeded);
    }

    [FixtureFact(Fixtures.BptChunkLog)]
    public void The_real_run_reports_rising_scan_progress()
    {
        var offsets = ParseRealLog()
            .Where(e => e.Kind == BptEventKind.ScanProgress)
            .Select(e => e.ByteOffset!.Value)
            .ToList();

        Assert.True(offsets.Count > 5, $"beklenenden az tarama satırı: {offsets.Count}");
        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal(0, offsets[0]);
    }

    [FixtureFact(Fixtures.BptChunkLog)]
    public void The_real_run_maps_onto_a_percentage()
    {
        // Ölçüm çalıştırmasında build kökü 512 MiB idi.
        const long total = 536_870_912;

        var percentages = ParseRealLog(total)
            .Where(e => e.Kind == BptEventKind.ScanProgress)
            .Select(e => e.Percent!.Value)
            .ToList();

        Assert.All(percentages, p => Assert.InRange(p, 0, 100));
        Assert.Equal(percentages.Order(), percentages);
    }

    [FixtureFact(Fixtures.BptChunkLog)]
    public void The_real_run_contains_no_failures()
    {
        Assert.DoesNotContain(ParseRealLog(), e => e.IsFailure);
    }

    /// <summary>Hiçbir satır düşürülmemeli; log paneli ham çıktıyı gösterebilmeli.</summary>
    [FixtureFact(Fixtures.BptChunkLog)]
    public void Every_non_blank_line_of_the_real_run_becomes_an_event()
    {
        var nonBlank = RealLog()
            .Split('\n')
            .Count(line => line.Trim().Length > 0);

        Assert.Equal(nonBlank, ParseRealLog().Count);
    }
}
