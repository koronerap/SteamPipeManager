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

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = await runner.RunAsync("+login anonymous +quit", ct: timeout.Token);

            output.WriteLine($"Deneme {attempt}: çıkış {result.ExitCode}, canlı log {(result.LiveLogFound ? "görüldü" : "GÖRÜLMEDİ")}, " +
                             $"{result.Events.Count} olay: {string.Join(", ", result.Events.Select(e => e.Kind).Distinct())}");

            if (result.ExitCode != SteamCmdInstallation.RestartRequiredExitCode)
            {
                break;
            }
        }

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
        output.WriteLine(Directory.Exists(Path.Combine(installation.Directory, "logs")) ? "evet (beklenmiyordu)" : "hayır");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.LiveLogFound, $"Canlı log beklenen yerde görülmedi: {installation.ConsoleLogPath}");
        Assert.Contains(result.Events, e => e.Kind == SteamCmdEventKind.LoginSucceeded);
    }
}
