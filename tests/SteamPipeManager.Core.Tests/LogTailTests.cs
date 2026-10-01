using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

public sealed class LogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_tail_{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "console_log.txt");

    public LogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    [Fact]
    public async Task Reads_lines_appended_while_running()
    {
        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var done = false;
        var lines = new List<string>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                Append(LogPath, $"satir {i}\r\n");
                await Task.Delay(30);
            }

            done = true;
        });

        await foreach (var line in tail.ReadLinesAsync(() => done).WithCancellation(TestTimeout()))
        {
            lines.Add(line);
        }

        await writer;

        Assert.Equal(["satir 0", "satir 1", "satir 2", "satir 3", "satir 4"], lines);
    }

    [Fact]
    public async Task Does_not_emit_partial_line_until_it_completes()
    {
        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var done = false;
        var lines = new List<string>();

        var writer = Task.Run(async () =>
        {
            Append(LogPath, "yarim");
            await Task.Delay(80);
            Append(LogPath, " satir\r\n");
            await Task.Delay(80);
            done = true;
        });

        await foreach (var line in tail.ReadLinesAsync(() => done).WithCancellation(TestTimeout()))
        {
            lines.Add(line);
        }

        await writer;

        Assert.Equal(["yarim satir"], lines);
    }

    [Fact]
    public async Task Emits_trailing_line_without_newline_at_end()
    {
        Append(LogPath, "son satir sonsuz");

        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var lines = new List<string>();

        await foreach (var line in tail.ReadLinesAsync(() => true).WithCancellation(TestTimeout()))
        {
            lines.Add(line);
        }

        Assert.Equal(["son satir sonsuz"], lines);
    }

    /// <summary>
    /// SteamCMD her başlangıçta bu dosyayı sıfırlıyor; küçülme algılanıp baştan okunmalı,
    /// yoksa yeni çalıştırmanın satırları hiç görünmez.
    /// </summary>
    [Fact]
    public async Task Restarts_from_beginning_when_file_is_truncated()
    {
        Append(LogPath, "eski calistirma satiri 1\r\neski satir 2\r\n");

        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var done = false;
        var lines = new List<string>();

        var writer = Task.Run(async () =>
        {
            await Task.Delay(80);
            File.WriteAllText(LogPath, "yeni calistirma\r\n");
            await Task.Delay(120);
            done = true;
        });

        await foreach (var line in tail.ReadLinesAsync(() => done).WithCancellation(TestTimeout()))
        {
            lines.Add(line);
        }

        await writer;

        Assert.Contains("eski calistirma satiri 1", lines);
        Assert.Contains("yeni calistirma", lines);
    }

    [Fact]
    public async Task Tolerates_missing_file()
    {
        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var lines = new List<string>();

        await foreach (var line in tail.ReadLinesAsync(() => true).WithCancellation(TestTimeout()))
        {
            lines.Add(line);
        }

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Tracks_growth_time_for_stall_detection()
    {
        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));
        var before = tail.LastGrowthAt;

        await Task.Delay(30);
        Append(LogPath, "yeni icerik\r\n");

        var done = false;
        var writer = Task.Run(async () => { await Task.Delay(80); done = true; });

        await foreach (var _ in tail.ReadLinesAsync(() => done).WithCancellation(TestTimeout()))
        {
        }

        await writer;

        Assert.True(tail.LastGrowthAt > before);
        Assert.True(tail.Position > 0);
    }

    /// <summary>
    /// Süreç bittikten sonraki son okumada dosya bir an kilitliyse (virüs tarayıcısı yeni
    /// yazılan dosyayı inceliyor) son satırlar kaybolmamalı. Eskiden bu okuma sessizce
    /// atlanıyordu ve oturum kontrolü sonucu hiç görmeden "giriş gerekli" diyordu.
    /// </summary>
    [Fact]
    public async Task The_final_read_waits_out_a_brief_lock_instead_of_dropping_the_last_lines()
    {
        await File.WriteAllTextAsync(LogPath, "Logging in user 'tester'\nFAILED (Rate Limit Exceeded)\n");
        var tail = new LogTail(LogPath, TimeSpan.FromMilliseconds(20));

        var exclusive = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            await exclusive.DisposeAsync();
        });

        var lines = new List<string>();

        // Süreç zaten bitmiş: ilk tur doğrudan son okuma.
        await foreach (var line in tail.ReadLinesAsync(() => true, TestTimeout()))
        {
            lines.Add(line);
        }

        Assert.Equal(["Logging in user 'tester'", "FAILED (Rate Limit Exceeded)"], lines);
    }

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
}
