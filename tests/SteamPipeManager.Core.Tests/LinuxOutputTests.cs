using System.Text;
using System.Threading.Channels;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Linux'taki SteamCMD çıktısı. Satırlar WSL'de Ubuntu 24.04 üzerinde gerçek SteamCMD
/// çalıştırılarak alındı (var olmayan bir hesapla; hesap adı burada değiştirildi):
/// renk kodları taşıyor ve giriş hatası "ERROR (...)" biçiminde.
/// </summary>
public sealed class LinuxOutputTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void A_failed_login_with_colour_codes_is_recognised()
    {
        var parser = new SteamCmdLogParser();

        var evt = parser.Feed($"{Esc}[0mLogging in user 'tester' [U:1:0] to Steam Public...{Esc}[0mERROR (Invalid Password)");

        Assert.NotNull(evt);
        Assert.Equal(SteamCmdEventKind.LoginFailed, evt!.Kind);
        Assert.Equal(LoginFailureReason.InvalidPassword, evt.FailureReason);
        Assert.Equal("tester", evt.Username);
    }

    [Fact]
    public void A_missing_cached_session_with_colour_codes_asks_for_interaction()
    {
        var evt = new SteamCmdLogParser().Feed($"{Esc}[0m{Esc}[1mCached credentials not found.");

        Assert.Equal(SteamCmdEventKind.NeedsInteraction, evt?.Kind);
    }

    [Fact]
    public void The_bootstrapper_localisation_warning_Linux_prints_every_run_is_not_an_error()
    {
        var evt = new SteamCmdLogParser().Feed("ILocalize::AddFile() failed to load file \"public/steambootstrapper_english.txt\".");

        Assert.NotEqual(SteamCmdEventKind.Error, evt?.Kind);
    }

    [Fact]
    public void A_successful_step_with_a_colour_code_before_OK_is_not_an_error()
    {
        var evt = new SteamCmdLogParser().Feed($"Loading Steam API...{Esc}[0mOK");

        Assert.NotEqual(SteamCmdEventKind.Error, evt?.Kind);
        Assert.NotEqual(SteamCmdEventKind.LoginFailed, evt?.Kind);
    }
}

public sealed class StreamLineReaderTests
{
    /// <summary>Parça parça veri veren, veri yoksa bekleyen bir okuyucu: canlı bir boru gibi.</summary>
    private sealed class ChunkedReader : TextReader
    {
        private readonly Channel<string?> _chunks = Channel.CreateUnbounded<string?>();

        public void Send(string chunk) => _chunks.Writer.TryWrite(chunk);

        public void Close() => _chunks.Writer.TryWrite(null);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            var chunk = await _chunks.Reader.ReadAsync(ct);

            if (chunk is null)
            {
                _chunks.Writer.TryWrite(null);
                return 0;
            }

            chunk.AsSpan().CopyTo(buffer.Span);
            return chunk.Length;
        }
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public async Task Lines_split_across_reads_are_joined()
    {
        var source = new ChunkedReader();
        var reader = new StreamLineReader(source);

        source.Send("Logging in us");
        source.Send("er 'tester'\r\nOK\n");
        source.Close();

        var lines = new List<string>();
        await foreach (var line in reader.ReadLinesAsync(Timeout()))
        {
            lines.Add(line);
        }

        Assert.Equal(["Logging in user 'tester'", "OK"], lines);
        Assert.Equal("Logging in user 'tester'\r\nOK\n", reader.Captured);
    }

    /// <summary>
    /// İstem satır sonu olmadan basılıyor ve araç yanıt bekliyor. Satırın tamamlanması
    /// beklenseydi istem hiç görülmez, giriş sonsuza kadar asılı kalırdı.
    /// </summary>
    [Fact]
    public async Task A_prompt_without_a_newline_is_delivered_while_the_tool_is_still_waiting()
    {
        var source = new ChunkedReader();
        var reader = new StreamLineReader(source);

        source.Send("Logging in user 'tester' to Steam Public...\nSteam Guard code:");

        await using var lines = reader.ReadLinesAsync(Timeout()).GetAsyncEnumerator();

        Assert.True(await lines.MoveNextAsync());
        Assert.Equal("Logging in user 'tester' to Steam Public...", lines.Current);

        // Kaynak kapanmadı; istem yine de geliyor.
        Assert.True(await lines.MoveNextAsync());
        Assert.Equal("Steam Guard code:", lines.Current);

        source.Close();
    }

    [Fact]
    public async Task An_ordinary_partial_line_ending_in_a_colon_waits_for_the_rest()
    {
        var source = new ChunkedReader();
        var reader = new StreamLineReader(source);

        source.Send("Update state (0x61) downloading, progress:");
        source.Send(" 42.00\n");
        source.Close();

        var lines = new List<string>();
        await foreach (var line in reader.ReadLinesAsync(Timeout()))
        {
            lines.Add(line);
        }

        Assert.Equal(["Update state (0x61) downloading, progress: 42.00"], lines);
    }
}
