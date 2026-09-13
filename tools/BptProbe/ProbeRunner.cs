using System.Diagnostics;
using System.Text;

namespace BptProbe;

/// <summary>Tek bir çalıştırmanın ölçüm sonucu.</summary>
public sealed record ProbeResult(
    string Name,
    int ExitCode,
    TimeSpan Duration,
    IReadOnlyList<(TimeSpan At, string Text)> StdoutChunks,
    IReadOnlyList<(TimeSpan At, string Text)> StderrChunks,
    bool TimedOut)
{
    public string Stdout => string.Concat(StdoutChunks.Select(c => c.Text));

    public string Stderr => string.Concat(StderrChunks.Select(c => c.Text));

    /// <summary>
    /// Tamponlama hakkında bu çalıştırmanın söyleyebildiği şey.
    ///
    /// Kısa süren, çıktısı tek parçaya sığan bir çalıştırma hiçbir şey kanıtlamaz:
    /// çıktı zaten tek seferde gelecek kadar küçüktür. Ölçüm ancak süreç bir süre
    /// çalışıp çıktıyı zamana yaydığında anlam kazanıyor — o yüzden kısa koşular
    /// "tamponlu" diye işaretlenmiyor.
    /// </summary>
    public BufferingVerdict Buffering
    {
        get
        {
            // Zamana yayılmış birden çok parça, akışın tamponlanmadığının doğrudan
            // kanıtı — çıktı ne kadar küçük olursa olsun.
            if (StdoutChunks.Count >= 2 &&
                StdoutChunks[^1].At - StdoutChunks[0].At > TimeSpan.FromMilliseconds(200))
            {
                return BufferingVerdict.Streaming;
            }

            // Tek parça hâlinde ve hızlı biten bir çalıştırma hiçbir şey kanıtlamaz:
            // çıktı zaten tek seferde gelecek kadar kısaydı.
            if (Duration < TimeSpan.FromMilliseconds(500))
            {
                return BufferingVerdict.Inconclusive;
            }

            // Süreç bir süre çalıştı ama çıktı ancak sonunda geldi: tamponlanıyor.
            return StdoutChunks.Count == 0 || StdoutChunks[0].At > Duration * 0.8
                ? BufferingVerdict.Buffered
                : BufferingVerdict.Inconclusive;
        }
    }
}

public enum BufferingVerdict
{
    /// <summary>Çalıştırma bu soruyu cevaplayacak kadar uzun/çıktılı değildi.</summary>
    Inconclusive,

    /// <summary>Çıktı sonda tek blok hâlinde geldi — canlı ilerleme stdout'tan okunamaz.</summary>
    Buffered,

    /// <summary>Çıktı zamana yayıldı — stdout canlı okunabilir.</summary>
    Streaming,
}

/// <summary>
/// BPT'yi çalıştırır ve çıktısını <b>zaman damgalı parçalar hâlinde</b> toplar.
///
/// Parçaların zamanlaması asıl ölçüm: hepsi sürecin sonunda tek blok hâlinde geliyorsa
/// stdout tamponlanıyordur ve ilerlemeyi oradan okuyamayız — Steam tarafında tam olarak
/// bu çıkmıştı ve çözüm log dosyasını takip etmek olmuştu.
/// </summary>
public sealed class ProbeRunner(string exePath, Report report)
{
    private readonly List<ProbeResult> _results = [];

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public async Task<ProbeResult> RunAsync(string name, string[] arguments)
    {
        Console.WriteLine($"→ {name}");

        // Çalışma dizinini aracın kendi klasörüne alıyoruz (log'unu oraya yazabilsin),
        // bu yüzden exe'nin mutlak yolu şart: göreli yol o dizine göre çözülmeye
        // çalışılır ve bulunamaz.
        var fullExePath = Path.GetFullPath(exePath);

        var startInfo = new ProcessStartInfo(fullExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(fullExePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Bir istem beklerse sonsuza kadar asılı kalmasın diye stdin yönlendirilip
            // hemen kapatılıyor.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)!;

        process.StandardInput.Close();

        var stdout = new List<(TimeSpan, string)>();
        var stderr = new List<(TimeSpan, string)>();

        var readOut = PumpAsync(process.StandardOutput, clock, stdout);
        var readErr = PumpAsync(process.StandardError, clock, stderr);

        var timedOut = false;

        using (var cts = new CancellationTokenSource(Timeout))
        {
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Bu arada kendi kapanmışsa sorun değil.
                }
            }
        }

        await Task.WhenAll(readOut, readErr);
        clock.Stop();

        var result = new ProbeResult(
            name,
            timedOut ? -1 : process.ExitCode,
            clock.Elapsed,
            stdout,
            stderr,
            timedOut);

        _results.Add(result);
        Write(result, arguments);

        return result;
    }

    /// <summary>
    /// Akışı satır satır değil <b>karakter blokları hâlinde</b> okur: istemler satır sonu
    /// ile bitmediği için ReadLine() onları hiç göstermez, ve blokların zamanı tamponlama
    /// davranışını ele verir.
    /// </summary>
    private static async Task PumpAsync(
        StreamReader reader,
        Stopwatch clock,
        List<(TimeSpan, string)> sink)
    {
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);

            if (read == 0)
            {
                break;
            }

            lock (sink)
            {
                sink.Add((clock.Elapsed, new string(buffer, 0, read)));
            }
        }
    }

    private void Write(ProbeResult result, string[] arguments)
    {
        report.Section($"ÇALIŞTIRMA: {result.Name}");
        report.Line($"argümanlar : {Redactor.Text(string.Join(" ", arguments))}");
        report.Line($"exit code  : {(result.TimedOut ? "ZAMAN AŞIMI" : result.ExitCode.ToString())}");
        report.Line($"süre       : {result.Duration.TotalMilliseconds:N0} ms");
        report.Line($"stdout     : {result.StdoutChunks.Count} parça, {result.Stdout.Length} karakter");
        report.Line($"stderr     : {result.StderrChunks.Count} parça, {result.Stderr.Length} karakter");
        report.Line($"tamponlama : {Describe(result.Buffering)}");

        if (result.StdoutChunks.Count > 1)
        {
            var timings = result.StdoutChunks.Take(8).Select(c => $"{c.At.TotalMilliseconds:N0}ms");
            report.Line($"parça anları: {string.Join(", ", timings)}");
        }

        report.Line("");
        report.Line("--- stdout ---");
        report.Line(Redactor.Text(result.Stdout).TrimEnd());

        if (result.Stderr.Trim().Length > 0)
        {
            report.Line("");
            report.Line("--- stderr ---");
            report.Line(Redactor.Text(result.Stderr).TrimEnd());
        }
    }

    private static string Describe(BufferingVerdict verdict) => verdict switch
    {
        BufferingVerdict.Buffered =>
            "TAMPONLU — çıktı sonda tek blok hâlinde geldi, canlı ilerleme stdout'tan okunamaz",
        BufferingVerdict.Streaming =>
            "akıyor — parçalar zamana yayılmış, stdout canlı okunabilir",
        _ => "belirsiz — çalıştırma bu soruyu cevaplayacak kadar uzun/çıktılı değildi",
    };

    public string Summary()
    {
        var lines = new StringBuilder();

        lines.AppendLine($"{_results.Count} çalıştırma yapıldı.");
        lines.AppendLine();
        lines.AppendLine($"{"çalıştırma",-34} {"exit",6} {"süre",10}  tamponlama");

        foreach (var result in _results)
        {
            var exit = result.TimedOut ? "t/a" : result.ExitCode.ToString();
            var verdict = result.Buffering switch
            {
                BufferingVerdict.Buffered => "TAMPONLU",
                BufferingVerdict.Streaming => "akıyor",
                _ => "belirsiz",
            };

            lines.AppendLine(
                $"{Trim(result.Name, 34),-34} {exit,6} {result.Duration.TotalMilliseconds,8:N0}ms  {verdict}");
        }

        lines.AppendLine();
        lines.AppendLine(
            "Not: yardım çağrıları kısa sürdüğü için tamponlama sorusunu cevaplayamaz.");
        lines.AppendLine(
            "Anlamlı olan uzun süren çalıştırmalardır — asıl cevap gerçek bir yüklemeden gelir.");

        return lines.ToString();

        static string Trim(string value, int max) =>
            value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
