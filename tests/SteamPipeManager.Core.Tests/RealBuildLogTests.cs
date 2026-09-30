using System.Runtime.CompilerServices;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Gerçek bir preview build'in kaydedilmiş SteamCMD çıktısına karşı doğrulama.
///
/// Bu log olmadan görülemeyecek üç fark vardı: preview başarısında BuildID yok,
/// SteamPipe satırları ikinci bir zaman damgası öneki taşıyor ve ilerleme
/// "......... 668.1MB (93%)" biçiminde basılıyor.
/// (Fixture'daki Steam ID gizlilik için sıfırlandı; biçim aynen korundu.)
/// </summary>
public class RealBuildLogTests
{
    private static string[] Lines([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "fixtures", "real_preview_build_console_log.txt");

            if (File.Exists(candidate))
            {
                return File.ReadAllLines(candidate);
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("Gerçek build log fixture'ı bulunamadı.");
    }

    private static SteamCmdEvent[] Parse() => [.. new SteamCmdLogParser().FeedAll(Lines())];

    /// <summary>
    /// Asıl hata buydu: preview başarı satırında BuildID yok, desen onu zorunlu tutuyordu
    /// ve build "tamamlanamadı (exit code 0)" olarak raporlanıyordu.
    /// </summary>
    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void Preview_success_is_recognised_without_a_build_id()
    {
        var success = Parse().SingleOrDefault(e => e.Kind == SteamCmdEventKind.BuildSucceeded);

        Assert.NotNull(success);
        Assert.Null(success!.BuildId);
        Assert.Contains("preview", success.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_build_success_still_captures_the_build_id()
    {
        var evt = new SteamCmdLogParser()
            .Feed("[2026-09-06 18:26:07] [2026-09-06 18:26:07]: Successfully finished AppID 1000 build (BuildID 12345).");

        Assert.NotNull(evt);
        Assert.Equal(SteamCmdEventKind.BuildSucceeded, evt!.Kind);
        Assert.Equal(12345u, evt.BuildId);
    }

    /// <summary>SteamPipe kendi satırlarına ikinci bir damga ekliyor; ikisi de ayrılmalı.</summary>
    [Fact]
    public void Strips_both_timestamp_prefixes()
    {
        var (timestamp, text) = SteamCmdLogParser.SplitTimestamp(
            "[2026-09-06 18:26:07] [2026-09-06 18:26:07]: Successfully finished AppID 1300000 build preview.");

        Assert.Equal("Successfully finished AppID 1300000 build preview.", text);
        Assert.Equal(new DateTime(2026, 9, 6, 18, 26, 7), timestamp!.Value.DateTime);
    }

    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void Detects_build_start()
    {
        var started = Parse().SingleOrDefault(e => e.Kind == SteamCmdEventKind.BuildStarted);

        Assert.NotNull(started);
        Assert.Contains("1300000", started!.Message);
    }

    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void Reads_scan_progress_percentages()
    {
        var percentages = Parse()
            .Where(e => e.Kind == SteamCmdEventKind.DepotProgress && e.Percent is not null)
            .Select(e => e.Percent!.Value)
            .ToList();

        Assert.Equal([12, 23, 35, 49, 60, 71, 82, 93], percentages);
    }

    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void Detects_login_depot_and_scanning()
    {
        var events = Parse();

        Assert.Contains(events, e => e.Kind == SteamCmdEventKind.LoginSucceeded);
        Assert.Contains(events, e => e.Kind == SteamCmdEventKind.ScanningContent);
        Assert.Contains(events, e => e.DepotId == 1300001u);
    }

    /// <summary>Yüzde taşımayan saf nokta satırları log panelini doldurmamalı.</summary>
    [Fact]
    public void Dot_only_progress_lines_are_dropped()
    {
        Assert.Null(new SteamCmdLogParser().Feed("[2026-09-06 18:26:06] ....."));
        Assert.Null(new SteamCmdLogParser().Feed("[2026-09-06 18:26:06] .........."));
    }

    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void No_failure_is_reported_for_a_successful_run()
    {
        var events = Parse();

        Assert.DoesNotContain(events, e => e.IsFailure);
        Assert.DoesNotContain(events, e => e.Kind == SteamCmdEventKind.NeedsInteraction);
    }

    /// <summary>Yerelleşen bootstrapper satırları hata sanılmamalı (M0 Bulgu 5).</summary>
    [FixtureFact(Fixtures.SteamCmdPreviewLog)]
    public void Localized_updater_lines_stay_informational()
    {
        var turkish = Parse().Where(e => e.Message.Contains("Güncellemeler") || e.Message.Contains("Yükleme"));

        Assert.NotEmpty(turkish);
        Assert.All(turkish, e => Assert.Equal(SteamCmdEventKind.Info, e.Kind));
    }
}
