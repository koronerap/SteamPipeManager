using System.Diagnostics;
using System.Runtime.CompilerServices;
using SteamPipeManager.Core.Epic;
using Xunit.Abstractions;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// <see cref="BptRunner"/>'ı <b>gerçek BuildPatchTool'a karşı</b> uçtan uca çalıştırır.
///
/// Bu mümkün çünkü <c>ChunkBuildDirectory</c> offline bir mod: Epic hesabı, kimlik
/// bilgisi ve ağ gerektirmiyor (BuildPatchTool 1.8.8 üzerinde, tools/BptProbe ile). Steam tarafında
/// aynı şeyi yapabilmek için <c>FakeSteamCmd</c> yazmak zorunda kalmıştık; burada
/// aracın kendisiyle test edebiliyoruz.
///
/// BuildPatchTool tescilli ve depoya girmiyor (gitignore'da). Bulunmadığı makinelerde
/// testler çalışmadan geçer; atlandıkları test çıktısına yazılır.
/// </summary>
public sealed class BptRunnerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"spm_bpt_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            try
            {
                Directory.Delete(_work, recursive: true);
            }
            catch (IOException)
            {
                // Geçici klasör temizlenemezse test sonucu etkilenmiyor.
            }
        }
    }

    /// <summary>Depodaki <c>tests/BuildPatchTool_*/</c> kurulumunu arar.</summary>
    private static string? FindTool([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var tests = Path.Combine(dir, "tests");

            if (Directory.Exists(tests))
            {
                foreach (var candidate in Directory.EnumerateDirectories(tests, "BuildPatchTool_*"))
                {
                    if (BptInstallation.LocateExecutable(candidate) is { } exe)
                    {
                        return exe;
                    }
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private BptInstallation? Tool()
    {
        if (FindTool() is { } exe)
        {
            return new BptInstallation(exe);
        }

        output.WriteLine("ATLANDI: tests/BuildPatchTool_*/ altında BuildPatchTool bulunamadı.");
        return null;
    }

    /// <summary>
    /// Sıkıştırılamayan içerik üretir: araç gerçekten chunk üretsin ve iş ölçülebilir
    /// kadar sürsün diye.
    /// </summary>
    private long CreateBuildRoot(int megabytes)
    {
        var root = Path.Combine(_work, "content");
        Directory.CreateDirectory(root);

        var random = new Random(4242);
        var buffer = new byte[1024 * 1024];
        long total = 0;

        foreach (var name in new[] { "Game.exe", "data_01.pak", "data_02.pak", "data_03.pak" })
        {
            using var stream = File.Create(Path.Combine(root, name));

            for (var mb = 0; mb < megabytes / 4; mb++)
            {
                random.NextBytes(buffer);
                stream.Write(buffer);
                total += buffer.Length;
            }
        }

        return total;
    }

    private BptCommand ChunkCommand() =>
        new BptCommand("ChunkBuildDirectory")
            .Add("FeatureLevel", "Latest")
            .Add("BuildRoot", Path.Combine(_work, "content"))
            .Add("CloudDir", Path.Combine(_work, "cloud"))
            .Add("ArtifactId", "spm-test-artifact")
            .Add("BuildVersion", "1.0.0-test")
            .Add("AppLaunch", "Game.exe")
            .Add("AppArgs", "");

    [Fact]
    public async Task A_real_run_reports_its_phases_and_succeeds()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var total = CreateBuildRoot(64);
        var logPath = Path.Combine(_work, "logs", "run.log");

        var runner = new BptRunner(tool) { TotalBytes = total };
        var seen = new List<BptEvent>();

        var result = await runner.RunAsync(
            ChunkCommand(), logPath, new Progress<BptEvent>(e => { lock (seen) { seen.Add(e); } }));

        output.WriteLine($"exit={result.ExitCode} olay={result.Events.Count}");

        Assert.True(result.Succeeded, $"çalıştırma başarısız: {result.Failure?.Message ?? result.StandardOutput}");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Events, e => e.IsFailure);

        var started = Assert.Single(result.Events, e => e.Kind == BptEventKind.Started);
        Assert.Equal("1.0.0-test", started.BuildVersion);
        Assert.Equal("spm-test-artifact", started.ArtifactId);

        Assert.Contains(result.Events, e => e.Kind == BptEventKind.FilesEnumerated);
        Assert.Contains(result.Events, e => e.Kind == BptEventKind.Succeeded);
    }

    /// <summary>
    /// Log'u <c>-abslog</c> ile kendi dosyamıza yönlendiriyoruz. Aksi hâlde bütün
    /// çalıştırmalar ortak bir dosyayı paylaşırdı — Steam tarafında tam olarak bu
    /// paylaşım yüzünden bir çalıştırmanın satırları başka bir hesaba mal edilmişti.
    /// </summary>
    [Fact]
    public async Task The_run_writes_to_the_log_file_we_chose()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        CreateBuildRoot(16);
        var logPath = Path.Combine(_work, "logs", "chosen.log");

        var result = await new BptRunner(tool).RunAsync(ChunkCommand(), logPath);

        Assert.True(File.Exists(logPath), "seçtiğimiz log dosyası yazılmadı");
        Assert.True(new FileInfo(logPath).Length > 0);
        Assert.Equal(logPath, result.LogPath);

        var text = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Beginning chunk generation", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asıl mesele bu: ilerleme <b>iş sürerken</b> gelmeli. stdout tamponlu olduğu için
    /// (Bulgu 1) olaylar log dosyasından okunuyor; bu test o mekanizmanın gerçekten
    /// canlı çalıştığını doğruluyor.
    /// </summary>
    [Fact]
    public async Task Progress_arrives_while_the_run_is_still_going()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var total = CreateBuildRoot(256);
        var logPath = Path.Combine(_work, "logs", "live.log");

        var clock = Stopwatch.StartNew();
        var arrivals = new List<TimeSpan>();

        var runner = new BptRunner(tool) { TotalBytes = total };

        var result = await runner.RunAsync(
            ChunkCommand(),
            logPath,
            new Progress<BptEvent>(_ => { lock (arrivals) { arrivals.Add(clock.Elapsed); } }));

        clock.Stop();

        Assert.True(result.Succeeded, "çalıştırma başarısız");
        Assert.NotEmpty(arrivals);

        var firstArrival = arrivals.Min();

        output.WriteLine(
            $"toplam {clock.Elapsed.TotalMilliseconds:N0} ms, ilk olay {firstArrival.TotalMilliseconds:N0} ms, " +
            $"{arrivals.Count} olay");

        // İlk olay sürecin ilk yarısında gelmiş olmalı; hepsi sonda gelseydi canlı
        // takip çalışmıyor demekti.
        Assert.True(
            firstArrival < clock.Elapsed * 0.5,
            $"ilk olay çok geç geldi: {firstArrival.TotalMilliseconds:N0} ms / {clock.Elapsed.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public async Task Scan_progress_is_reported_as_a_rising_percentage()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var total = CreateBuildRoot(256);
        var logPath = Path.Combine(_work, "logs", "percent.log");

        var runner = new BptRunner(tool) { TotalBytes = total };
        var result = await runner.RunAsync(ChunkCommand(), logPath);

        var percentages = result.Events
            .Where(e => e.Kind == BptEventKind.ScanProgress)
            .Select(e => e.Percent!.Value)
            .ToList();

        Assert.True(percentages.Count > 1, $"beklenenden az ilerleme olayı: {percentages.Count}");
        Assert.All(percentages, p => Assert.InRange(p, 0, 100));
        Assert.Equal(percentages.Order(), percentages);
    }

    /// <summary>
    /// Zorunlu parametreler eksikken araç hata koduyla çıkıyor ve sebebini sembolik
    /// adıyla bildiriyor; runner bunu başarısızlık olarak raporlamalı.
    /// </summary>
    /// <summary>
    /// Döngüyü kapatan test: <see cref="EpicBuildVersion.Normalize"/> aracın sürüm
    /// dizesine ne yapacağını <b>tahmin ediyor</b> (boşlukları siliyor). Bu tahmin
    /// yanlışsa kullanıcıya "şu şekilde kaydedilecek" diye yanlış bilgi veririz ve
    /// benzersizlik kontrolümüz çakışmayı kaçırır.
    ///
    /// Manifest dosyasının adı <c>&lt;artifact&gt;&lt;sürüm&gt;.manifest</c> biçiminde
    /// olduğu için aracın sürümü nasıl yazdığı oradan doğrulanabiliyor.
    /// </summary>
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-beta.4")]
    [InlineData("1.2.3 beta")]
    [InlineData("build_2026_09_10")]
    public async Task Our_prediction_of_the_recorded_version_matches_the_tool(string version)
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        CreateBuildRoot(8);

        var cloud = Path.Combine(_work, $"cloud_{Guid.NewGuid():N}");

        var command = new BptCommand("ChunkBuildDirectory")
            .Add("FeatureLevel", "Latest")
            .Add("BuildRoot", Path.Combine(_work, "content"))
            .Add("CloudDir", cloud)
            .Add("ArtifactId", "verify")
            .Add("BuildVersion", version)
            .Add("AppLaunch", "Game.exe")
            .Add("AppArgs", "");

        var result = await new BptRunner(tool).RunAsync(
            command, Path.Combine(_work, "logs", $"ver_{Guid.NewGuid():N}.log"));

        Assert.True(result.Succeeded, $"çalıştırma başarısız: {result.Failure?.Message}");

        var manifest = Assert.Single(Directory.GetFiles(cloud, "*.manifest"));
        var expected = $"verify{EpicBuildVersion.Normalize(version)}.manifest";

        output.WriteLine($"girilen='{version}' → dosya='{Path.GetFileName(manifest)}'");

        Assert.Equal(expected, Path.GetFileName(manifest));
    }

    [Fact]
    public async Task A_run_missing_required_arguments_fails_with_a_named_reason()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var logPath = Path.Combine(_work, "logs", "invalid.log");

        var result = await new BptRunner(tool).RunAsync(
            new BptCommand("ChunkBuildDirectory").Add("BuildVersion", "1.0.0"), logPath);

        Assert.False(result.Succeeded);
        Assert.NotEqual(0, result.ExitCode);

        output.WriteLine($"exit={result.ExitCode} sebep={result.Failure?.ExitReason}");
        Assert.NotNull(result.Failure);
    }
}
