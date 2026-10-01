using System.Diagnostics;
using System.Text;

using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>Bir SteamCMD çalıştırmasının sonucu.</summary>
public sealed record SteamCmdRunResult(
    int ExitCode,
    IReadOnlyList<SteamCmdEvent> Events,
    string RawOutput,
    bool TimedOut,
    bool Cancelled)
{
    /// <summary>
    /// Canlı log beklenen yerde görüldü mü. Görülmediyse olaylar süreç bittikten sonra
    /// stdout'tan çıkarıldı: sonuç doğru ama ilerleme canlı gösterilemedi.
    /// </summary>
    public bool LiveLogFound { get; init; }

    public bool SawInteractionPrompt =>
        Events.Any(e => e.Kind == SteamCmdEventKind.NeedsInteraction);

    public SteamCmdEvent? SuccessEvent =>
        Events.LastOrDefault(e => e.Kind == SteamCmdEventKind.BuildSucceeded);

    public SteamCmdEvent? LoginFailure =>
        Events.LastOrDefault(e => e.Kind == SteamCmdEventKind.LoginFailed);
}

/// <summary>
/// SteamCMD'yi çalıştırır ve <c>console_log.txt</c> üzerinden canlı olay akışı verir.
///
/// stdin'e hiçbir şey yazılmaz: build'ler oturum cache'i sayesinde etkileşimsizdir.
/// SteamCMD yine de girdi isterse (oturum düşmüşse) bu bir hata olarak raporlanır,
/// çünkü istem bize ulaşmaz ve süreç sonsuza kadar bekler (M0 Bulgu 3).
/// </summary>
public sealed class SteamCmdRunner(SteamCmdInstallation installation)
{
    public SteamCmdInstallation Installation { get; } = installation;

    /// <summary>Bu süre boyunca log büyümezse çalıştırma donmuş sayılır.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public async Task<SteamCmdRunResult> RunAsync(
        string arguments,
        IProgress<SteamCmdEvent>? progress = null,
        CancellationToken ct = default)
    {
        var logStart = Installation.ResetConsoleLog();
        Directory.CreateDirectory(Installation.LogsDirectory);

        var startInfo = new ProcessStartInfo(Installation.ExecutablePath, arguments)
        {
            WorkingDirectory = Installation.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // stdin yönlendirilir ama yazılmaz: SteamCMD'nin konsolu miras alıp
            // kullanıcının ekranına istem basmasını engeller.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Installation.Prepare(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start SteamCMD.");

        // Canlı kaynak platforma göre: Windows'ta console_log.txt, Linux/macOS'ta stdout
        // (bkz. SteamCmdInstallation.LiveOutputIsStandardOutput).
        var fromStdout = Installation.LiveOutputIsStandardOutput;

        var stdout = new StringBuilder();
        var stdoutReader = fromStdout ? new StreamLineReader(process.StandardOutput) : null;
        var stdoutTask = fromStdout ? Task.CompletedTask : DrainAsync(process.StandardOutput, stdout);
        var stderrTask = DrainAsync(process.StandardError, stdout);

        var events = new List<SteamCmdEvent>();
        var parser = new SteamCmdLogParser();
        var tail = new LogTail(Installation.ConsoleLogPath, startPosition: logStart);

        using var stallSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timedOut = false;

        var watchdog = stdoutReader is not null
            ? WatchStallAsync(() => true, () => stdoutReader.LastGrowthAt, process, stallSource, () => timedOut = true)
            : WatchStallAsync(() => tail.HasSeenFile, () => tail.LastGrowthAt, process, stallSource, () => timedOut = true);

        var lines = stdoutReader is not null
            ? stdoutReader.ReadLinesAsync(stallSource.Token)
            : tail.ReadLinesAsync(() => process.HasExited, stallSource.Token);

        try
        {
            await foreach (var line in lines)
            {
                if (parser.Feed(line) is not { } evt)
                {
                    continue;
                }

                events.Add(evt);
                progress?.Report(evt);

                // Görünmeyen bir istemde sonsuza kadar beklemektense hemen durdurulur.
                if (evt.Kind == SteamCmdEventKind.NeedsInteraction)
                {
                    KillQuietly(process);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Aşağıda iptal/zaman aşımı olarak raporlanır.
        }

        var cancelled = ct.IsCancellationRequested;

        if (!process.HasExited)
        {
            KillQuietly(process);
        }

        await stallSource.CancelAsync();
        await watchdog;
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(CancellationToken.None);

        if (stdoutReader is not null)
        {
            stdout.Insert(0, stdoutReader.Captured);
        }

        // Canlı log'dan hiç olay çıkmadıysa (bir platformda SteamCMD log'unu başka yere
        // yazıyorsa ya da dosya son okumada erişilemediyse) sonuç yine de kaybolmasın:
        // stdout tamponlu ama süreç bitince eksiksiz, aynı satırlar oradan ayrıştırılıyor.
        if (events.Count == 0)
        {
            foreach (var evt in new SteamCmdLogParser().FeedAll(stdout.ToString().Split('\n')))
            {
                events.Add(evt);
                progress?.Report(evt);
            }
        }

        return new SteamCmdRunResult(
            process.ExitCode, events, stdout.ToString(), timedOut, cancelled)
        {
            LiveLogFound = stdoutReader is not null ? stdoutReader.Captured.Length > 0 : tail.HasSeenFile,
        };
    }

    /// <summary>
    /// stdout tamponlu geldiği için canlı gösterimde kullanılmaz, ama arşiv olarak saklanır
    /// ve okunmazsa pipe dolup süreci bloklayabilir.
    /// </summary>
    private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        var text = await reader.ReadToEndAsync();

        lock (sink)
        {
            sink.Append(text);
        }
    }

    private async Task WatchStallAsync(
        Func<bool> sourceSeen,
        Func<DateTimeOffset> lastGrowth,
        Process process,
        CancellationTokenSource source,
        Action onStalled)
    {
        try
        {
            while (!source.IsCancellationRequested && !process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), source.Token);

                // Log hiç görünmediyse "büyümüyor" bilgisi anlamsız; çalışan bir build'i
                // takıldı sanıp öldürmemek için bekçi devreye girmiyor.
                if (!sourceSeen() || DateTimeOffset.UtcNow - lastGrowth() < StallTimeout)
                {
                    continue;
                }

                onStalled();
                KillQuietly(process);
                await source.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal sonlanma.
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Süreç zaten bitmiş.
        }
    }
}
