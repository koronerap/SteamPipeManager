using System.Diagnostics;
using System.Text;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Epic;

public sealed record BptRunResult(
    int ExitCode,
    IReadOnlyList<BptEvent> Events,
    string LogPath,
    string StandardOutput,
    bool TimedOut,
    bool Cancelled)
{
    /// <summary>Aracın bildirdiği ilk başarısızlık; yoksa null.</summary>
    public BptEvent? Failure => Events.FirstOrDefault(e => e.IsFailure);

    public bool Succeeded =>
        !TimedOut && !Cancelled && ExitCode == 0 && Failure is null;
}

/// <summary>
/// BuildPatchTool'u çalıştırır ve ilerlemesini <b>log dosyasından</b> canlı olarak yayar.
///
/// Neden stdout'tan değil: ölçüldü (BuildPatchTool 1.8.8 üzerinde, tools/BptProbe ile) — çıktı pipe'a
/// bağlıyken tamponlanıyor, 1.4 saniyelik bir çalıştırmada bütün satırlar son 150 ms'de
/// geldi. Buna karşılık aracın yazdığı log dosyası iş sürerken artımlı büyüyor
/// (Bulgu 2). SteamCMD'de bulduğumuz tablonun aynısı, çözüm de aynı.
///
/// stdout yine de okunuyor: okunmazsa pipe dolup süreci bloklayabilir, ayrıca hata
/// özetleri orada da bulunuyor.
/// </summary>
public sealed class BptRunner(BptInstallation installation)
{
    public BptInstallation Installation { get; } = installation;

    /// <summary>Log bu süre boyunca büyümezse çalıştırma donmuş sayılır.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tarama ilerlemesini yüzdeye çevirebilmek için build kökünün toplam boyutu.
    /// Araç yüzde bildirmiyor, yalnızca taranan bayt konumunu.
    /// </summary>
    public long? TotalBytes { get; set; }

    public async Task<BptRunResult> RunAsync(
        BptCommand command,
        string logPath,
        IProgress<BptEvent>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Installation.Exists)
        {
            throw new FileNotFoundException(
                "BuildPatchTool bulunamadı.", Installation.ExecutablePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // Her çalıştırma kendi log dosyasını alıyor; önceki denemeden kalan varsa
        // silinerek karışması engelleniyor.
        DeleteQuietly(logPath);

        var withLog = command.WithLogFile(logPath);

        var startInfo = new ProcessStartInfo(Path.GetFullPath(Installation.ExecutablePath))
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Installation.ExecutablePath)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Araç bir istem beklerse sonsuza kadar asılı kalmasın diye stdin
            // yönlendirilip hemen kapatılıyor.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in withLog.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Secret yalnızca çocuk sürecin ortamında; ne komut satırında ne de bizim
        // sürecimizin ortamında görünüyor.
        if (withLog.ClientSecret is { Length: > 0 } secret)
        {
            startInfo.Environment[BptCommand.SecretEnvironmentVariable] = secret;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("BuildPatchTool başlatılamadı.");

        process.StandardInput.Close();

        var output = new StringBuilder();
        var drainOut = DrainAsync(process.StandardOutput, output);
        var drainError = DrainAsync(process.StandardError, output);

        var events = new List<BptEvent>();
        var parser = new BptLogParser { TotalBytes = TotalBytes };
        var tail = new LogTail(logPath, TimeSpan.FromMilliseconds(200));

        using var stallSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timedOut = false;

        var watchdog = WatchStallAsync(tail, process, stallSource, () => timedOut = true);

        try
        {
            await foreach (var line in tail.ReadLinesAsync(() => process.HasExited, stallSource.Token))
            {
                if (parser.Feed(line) is not { } evt)
                {
                    continue;
                }

                events.Add(evt);
                progress?.Report(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Aşağıda iptal/zaman aşımı olarak raporlanıyor.
        }

        var cancelled = ct.IsCancellationRequested;

        if (!process.HasExited)
        {
            KillQuietly(process);
        }

        await stallSource.CancelAsync();
        await watchdog;
        await Task.WhenAll(drainOut, drainError);
        await process.WaitForExitAsync(CancellationToken.None);

        return new BptRunResult(
            process.ExitCode, events, logPath, output.ToString(), timedOut, cancelled);
    }

    /// <summary>
    /// stdout tamponlu geldiği için canlı gösterimde kullanılmıyor, ama okunmazsa
    /// pipe dolup süreci bloklayabilir; ayrıca arşiv değeri var.
    /// </summary>
    private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        var text = await reader.ReadToEndAsync();

        lock (sink)
        {
            sink.Append(text);
        }
    }

    /// <summary>
    /// Log büyümeyi durdurduysa çalıştırma donmuş demektir. Görünmeyen bir istemde
    /// sonsuza kadar beklemektense süreç durduruluyor.
    /// </summary>
    private async Task WatchStallAsync(
        LogTail tail,
        Process process,
        CancellationTokenSource source,
        Action onStalled)
    {
        try
        {
            while (!source.IsCancellationRequested && !process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), source.Token);

                if (DateTimeOffset.UtcNow - tail.LastGrowthAt <= StallTimeout)
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
            // Normal kapanış.
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Bu arada kendi kapanmışsa sorun değil.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Silinemezse LogTail küçülme tespitiyle baş edebiliyor.
        }
    }
}
