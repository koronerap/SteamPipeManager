using SteamPipeManager.Core.Platform;
using SteamPipeManager.Core.SteamCmd;
using Xunit.Abstractions;

namespace SteamPipeManager.Core.Tests;

/// <summary>Yalnızca <c>SPM_REAL_STEAMCMD=1</c> ile çalışan test: ağ ve Valve'ın sunucuları gerekiyor.</summary>
public sealed class RealSteamCmdFactAttribute : FactAttribute
{
    public RealSteamCmdFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPM_REAL_STEAMCMD") != "1")
        {
            Skip = "Gerçek SteamCMD ölçümü; SPM_REAL_STEAMCMD=1 ile açılıyor.";
        }
    }
}

/// <summary>
/// Gerçek SteamCMD'ye karşı ölçüm — Linux ve macOS desteğinin dayandığı varsayımlar:
/// Valve'ın paketi bu platformda iniyor ve açılıyor, SteamCMD uygulamanın verdiği
/// HOME'u kullanıyor ve canlı log'u tam beklenen yere (<see cref="SteamCmdInstallation.ConsoleLogPath"/>)
/// yazıyor. Windows'taki M0 ölçümlerinin karşılığı; hesap gerektirmiyor (anonim giriş).
/// </summary>
public sealed class RealSteamCmdTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spm-real-steamcmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // CI makinesi zaten atılıyor.
        }
    }

    [RealSteamCmdFact]
    public async Task SteamCmd_installs_runs_and_writes_its_live_log_where_expected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var exe = await new SteamCmdProvisioner().EnsureInstalledAsync(Path.Combine(_root, "steamcmd"), ct: timeout.Token);
        var installation = new SteamCmdInstallation(exe, homeDirectory: Path.Combine(_root, "home"));

        output.WriteLine($"Platform:        {HostPlatform.Current.RuntimeId}");
        output.WriteLine($"Çalıştırılabilir: {exe}");
        output.WriteLine($"Beklenen log:    {installation.ConsoleLogPath}");

        var runner = new SteamCmdRunner(installation) { StallTimeout = TimeSpan.FromMinutes(5) };

        // İlk çalıştırma kendini güncelliyor ve çoğu zaman "yeniden başlat" koduyla çıkıyor.
        SteamCmdRunResult result = null!;

        var arrivals = new List<DateTimeOffset>();
        var started = DateTimeOffset.UtcNow;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            arrivals.Clear();
            started = DateTimeOffset.UtcNow;

            // Olaylar geldiği an kaydediliyor: hepsi süreç bitince bir arada geliyorsa
            // kaynak tamponlu demektir, canlı ilerleme gösterilemez.
            result = await runner.RunAsync(
                "+login anonymous +app_info_update 1 +quit",
                new SyncProgress(_ => arrivals.Add(DateTimeOffset.UtcNow)),
                timeout.Token);

            output.WriteLine($"Deneme {attempt}: çıkış {result.ExitCode}, canlı log {(result.LiveLogFound ? "görüldü" : "GÖRÜLMEDİ")}, " +
                             $"{result.Events.Count} olay: {string.Join(", ", result.Events.Select(e => e.Kind).Distinct())}");

            if (result.ExitCode != SteamCmdInstallation.RestartRequiredExitCode)
            {
                break;
            }
        }

        var spreadSeconds = arrivals.Count > 1 ? (arrivals[^1] - arrivals[0]).TotalSeconds : 0;
        var distinctSeconds = arrivals.Select(a => (long)(a - started).TotalSeconds).Distinct().Count();
        output.WriteLine($"Canlılık:        {arrivals.Count} olay {spreadSeconds:N1} saniyeye, {distinctSeconds} farklı saniyeye yayıldı " +
                         $"({(distinctSeconds > 2 ? "canlı" : "TAMPONLU görünüyor")})");

        output.WriteLine("--- hata sayılan satırlar ---");
        foreach (var error in result.Events.Where(e => e.Kind == SteamCmdEventKind.Error).Take(15))
        {
            output.WriteLine(error.Message);
        }

        output.WriteLine("--- stdout'un ilk satırları ---");
        foreach (var line in result.RawOutput.Split('\n').Take(25))
        {
            output.WriteLine(line.TrimEnd('\r'));
        }

        output.WriteLine("--- ev dizinindeki log ve config dosyaları ---");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "home"), "*", SearchOption.AllDirectories)
                     .Where(f => f.Contains("logs") || f.EndsWith("config.vdf", StringComparison.Ordinal))
                     .Take(30))
        {
            output.WriteLine(Path.GetRelativePath(_root, file));
        }

        output.WriteLine("--- kurulum klasöründe 'logs' var mı ---");
        var installLogs = Path.Combine(installation.Directory, "logs");
        output.WriteLine(Directory.Exists(installLogs)
            ? "evet: " + string.Join(", ", Directory.EnumerateFiles(installLogs).Select(Path.GetFileName))
            : "hayır");

        WriteStderrFile(installation);

        Assert.Equal(0, result.ExitCode);
        Assert.True(distinctSeconds > 2, "Olaylar canlı gelmedi; süreç bitince bir arada geldi.");
        Assert.True(result.LiveLogFound, $"Canlı log beklenen yerde görülmedi: {installation.ConsoleLogPath}");
        Assert.Contains(result.Events, e => e.Kind == SteamCmdEventKind.LoginSucceeded);
    }

    /// <summary>
    /// Giriş hatası canlı kaynağa düşüyor mu. SteamCMD Linux ve macOS'ta stderr'i bir
    /// dosyaya yönlendiriyor ("Redirecting stderr to ..."); hata oraya giderse uygulama
    /// yanlış şifreyi göremez, giriş belirsiz biçimde biterdi. Var olmayan bir hesapla
    /// tek deneme.
    /// </summary>
    [RealSteamCmdFact]
    public async Task A_failed_login_reaches_the_live_output()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var exe = await new SteamCmdProvisioner().EnsureInstalledAsync(Path.Combine(_root, "steamcmd"), ct: timeout.Token);
        var installation = new SteamCmdInstallation(exe, homeDirectory: Path.Combine(_root, "home"));
        var runner = new SteamCmdRunner(installation) { StallTimeout = TimeSpan.FromMinutes(5) };

        SteamCmdRunResult result = null!;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = await runner.RunAsync($"+login spm_ci_{Guid.NewGuid():N} not-a-password +quit", ct: timeout.Token);

            output.WriteLine($"Deneme {attempt}: çıkış {result.ExitCode}, " +
                             $"{result.Events.Count} olay: {string.Join(", ", result.Events.Select(e => e.Kind).Distinct())}");

            if (result.ExitCode != SteamCmdInstallation.RestartRequiredExitCode)
            {
                break;
            }
        }

        output.WriteLine("--- giriş satırları ---");
        foreach (var evt in result.Events.Where(e => e.Kind is SteamCmdEventKind.LoginFailed or SteamCmdEventKind.Error))
        {
            output.WriteLine($"{evt.Kind} ({evt.FailureReason}): {evt.Message}");
        }

        WriteStderrFile(installation);

        Assert.Contains(result.Events, e => e.Kind == SteamCmdEventKind.LoginFailed);
    }

    private void WriteStderrFile(SteamCmdInstallation installation)
    {
        var path = Directory.EnumerateFiles(Path.Combine(_root, "home"), "stderr.txt", SearchOption.AllDirectories).FirstOrDefault();

        output.WriteLine($"--- stderr.txt ({(path is null ? "yok" : new FileInfo(path).Length + " bayt")}) ---");

        if (path is not null)
        {
            foreach (var line in File.ReadLines(path).Take(20))
            {
                output.WriteLine(line);
            }
        }
    }
}

file sealed class SyncProgress(Action<SteamCmdEvent> report) : IProgress<SteamCmdEvent>
{
    public void Report(SteamCmdEvent value) => report(value);
}
