using System.Diagnostics;
using System.Text;

namespace BptProbe;

/// <summary>
/// Asıl ölçüm: uzun süren gerçek bir işin ilerlemesi <b>nereden</b> okunabiliyor?
///
/// <c>ChunkBuildDirectory</c> offline bir mod — Epic hesabı, client id/secret, ağ
/// gerektirmiyor. Bu sayede hesabı olmayan biri bile aracın canlı davranışını
/// ölçebiliyor: stdout tamponlanıyor mu, log dosyası iş sürerken mi yazılıyor,
/// ilerleme satırları neye benziyor.
///
/// Steam tarafında bu ölçümün cevabı bütün canlı ilerleme tasarımını belirlemişti.
/// </summary>
public static class OfflineRun
{
    /// <summary>BPT'nin log'unu yazdığı yer; araç hata mesajlarında da bu yolu söylüyor.</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BuildPatchTool", "Saved", "Logs", "BuildPatchTool.log");

    public static async Task RunAsync(string exePath, int sizeMegabytes, Report report)
    {
        var buildRoot = Path.Combine(Path.GetTempPath(), $"bpt_offline_root_{Guid.NewGuid():N}");
        var cloudDir = Path.Combine(Path.GetTempPath(), $"bpt_offline_cloud_{Guid.NewGuid():N}");

        report.Section($"OFFLINE ÇALIŞTIRMA: ChunkBuildDirectory ({sizeMegabytes} MB)");
        Console.WriteLine($"→ offline chunk çalıştırması ({sizeMegabytes} MB test verisi üretiliyor)");

        try
        {
            var bytes = CreateBuildRoot(buildRoot, sizeMegabytes);
            Directory.CreateDirectory(cloudDir);
            report.Line($"test verisi : {bytes:N0} bayt, {Directory.GetFiles(buildRoot).Length} dosya");

            // Sıkıştırılamayan rastgele veri kullanılıyor: BPT'nin gerçekten chunk
            // üretmesi ve işin ölçülebilir kadar sürmesi için.
            var startInfo = new ProcessStartInfo(Path.GetFullPath(exePath))
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var argument in new[]
                     {
                         "-mode=ChunkBuildDirectory",
                         "-FeatureLevel=Latest",
                         $"-BuildRoot={buildRoot}",
                         $"-CloudDir={cloudDir}",
                         "-ArtifactId=probe-artifact",
                         "-BuildVersion=0.0.0-probe",
                         "-AppLaunch=Game.exe",
                         "-AppArgs=",
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Log'un bu çalıştırmaya ait olduğundan emin olmak için önceki boyut alınır.
            var logSizeBefore = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;

            var clock = Stopwatch.StartNew();
            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();

            var stdoutChunks = new List<(TimeSpan At, string Text)>();
            var stdout = PumpAsync(process.StandardOutput, clock, stdoutChunks);
            var stderr = process.StandardError.ReadToEndAsync();

            // Süreç çalışırken log dosyasının boyutu örnekleniyor. Boyut iş sürerken
            // artıyorsa canlı ilerleme oradan okunabilir demektir.
            var samples = new List<(TimeSpan At, long Size)>();

            while (!process.HasExited)
            {
                samples.Add((clock.Elapsed, CurrentLogSize()));
                await Task.Delay(200);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            clock.Stop();

            samples.Add((clock.Elapsed, CurrentLogSize()));

            Report(report, process.ExitCode, clock.Elapsed, stdoutChunks, samples, logSizeBefore);
        }
        catch (Exception ex)
        {
            report.Line($"offline çalıştırma başarısız: {ex.Message}");
        }
        finally
        {
            Delete(buildRoot);
            Delete(cloudDir);
        }
    }

    private static void Report(
        Report report,
        int exitCode,
        TimeSpan duration,
        List<(TimeSpan At, string Text)> stdoutChunks,
        List<(TimeSpan At, long Size)> samples,
        long logSizeBefore)
    {
        var text = string.Concat(stdoutChunks.Select(c => c.Text));

        report.Line($"exit code   : {exitCode}");
        report.Line($"süre        : {duration.TotalMilliseconds:N0} ms");
        report.Line("");

        report.Line("--- stdout zamanlaması ---");
        report.Line($"{stdoutChunks.Count} parça, {text.Length} karakter");

        if (stdoutChunks.Count > 0)
        {
            report.Line($"ilk parça   : {stdoutChunks[0].At.TotalMilliseconds:N0} ms");
            report.Line($"son parça   : {stdoutChunks[^1].At.TotalMilliseconds:N0} ms");
            report.Line(stdoutChunks[0].At > duration * 0.8
                ? "SONUÇ: stdout TAMPONLU — çıktı ancak sürecin sonunda geldi."
                : "SONUÇ: stdout akıyor.");
        }

        report.Line("");
        report.Line("--- log dosyası büyümesi ---");
        report.Line($"log yolu    : {Redactor.Path(LogPath)}");
        report.Line($"önceki boyut: {logSizeBefore:N0} bayt");

        var growthPoints = 0;

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].Size != samples[i - 1].Size)
            {
                growthPoints++;
            }
        }

        foreach (var (at, size) in samples.Where((_, i) => i % Math.Max(1, samples.Count / 12) == 0))
        {
            report.Line($"  {at.TotalMilliseconds,8:N0} ms  {size,12:N0} bayt");
        }

        report.Line("");
        report.Line(growthPoints > 1
            ? $"SONUÇ: log dosyası iş sürerken {growthPoints} kez büyüdü — CANLI TAKİP EDİLEBİLİR."
            : "SONUÇ: log dosyası iş sürerken büyümedi; ancak sonda yazılıyor olabilir.");

        report.Line("");
        report.Line("--- stdout ---");
        report.Line(Redactor.Text(text).TrimEnd());

        if (File.Exists(LogPath))
        {
            report.Line("");
            report.Line("--- log dosyası (son 120 satır) ---");

            var lines = SafeReadLines(LogPath);
            report.Line(Redactor.Text(string.Join(Environment.NewLine, lines.TakeLast(120))));
        }
    }

    private static long CurrentLogSize()
    {
        try
        {
            return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static string[] SafeReadLines(string path)
    {
        try
        {
            // BPT hâlâ tutuyor olabilir; tam paylaşımla açılıyor.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException)
        {
            return ["(log okunamadı)"];
        }
    }

    /// <summary>
    /// Sıkıştırılamayan rastgele içerikli dosyalar üretir; BPT'nin gerçekten iş
    /// yapması ve sürenin ölçülebilir olması için.
    /// </summary>
    private static long CreateBuildRoot(string directory, int sizeMegabytes)
    {
        Directory.CreateDirectory(directory);

        var random = new Random(1234);
        var buffer = new byte[1024 * 1024];
        long total = 0;

        // Tek dev dosya yerine birkaç dosya: dosya tarama aşaması da görünür olsun.
        const int fileCount = 8;
        var perFile = Math.Max(1, sizeMegabytes / fileCount);

        for (var i = 0; i < fileCount; i++)
        {
            var name = i == 0 ? "Game.exe" : $"data_{i:00}.pak";

            using var stream = File.Create(Path.Combine(directory, name));

            for (var mb = 0; mb < perFile; mb++)
            {
                random.NextBytes(buffer);
                stream.Write(buffer);
                total += buffer.Length;
            }
        }

        return total;
    }

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

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temizlik başarısız olsa da ölçüm geçerli.
        }
    }
}
