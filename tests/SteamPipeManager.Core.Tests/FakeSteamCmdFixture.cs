using System.Runtime.CompilerServices;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Sahte steamcmd'yi izole bir klasöre kopyalar ve <see cref="SteamCmdInstallation"/>
/// olarak sunar. Her test kendi kopyasını alır: <c>logs/console_log.txt</c> ve
/// <c>scenario.txt</c> testler arasında karışmaz.
/// </summary>
internal sealed class FakeSteamCmd : IDisposable
{
    private readonly string _dir;

    private FakeSteamCmd(string dir, string exePath)
    {
        _dir = dir;
        Installation = new SteamCmdInstallation(exePath);
    }

    public SteamCmdInstallation Installation { get; }

    public static FakeSteamCmd Create(string scenario)
    {
        var source = LocateBuildOutput();
        var dir = Path.Combine(Path.GetTempPath(), $"spm_fake_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);

            // Sadece çalıştırmak için gerekenler; log ve senaryo dosyaları kopyalanmaz.
            if (name is "scenario.txt" || Path.GetExtension(name) is ".pdb")
            {
                continue;
            }

            File.Copy(file, Path.Combine(dir, name), overwrite: true);
        }

        File.WriteAllText(Path.Combine(dir, "scenario.txt"), scenario);

        // Kurulum gerçek aracın adını bekliyor (Windows'ta steamcmd.exe, Linux/macOS'ta
        // steamcmd.sh); sahte aracın kopyası o adla bırakılıyor. Uygulama ana bilgisayarı
        // (apphost) hangi dll'i çalıştıracağını kendi adından değil içine gömülü addan
        // bildiği için kopya da çalışıyor; Unix'te kopyalama çalıştırma iznini koruyor.
        var fakeExe = Path.Combine(dir, OperatingSystem.IsWindows() ? "FakeSteamCmd.exe" : "FakeSteamCmd");
        var steamCmdExe = Path.Combine(dir, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");

        if (File.Exists(fakeExe))
        {
            File.Copy(fakeExe, steamCmdExe, overwrite: true);
        }

        return new FakeSteamCmd(dir, File.Exists(steamCmdExe) ? steamCmdExe : fakeExe);
    }

    /// <summary>
    /// FakeSteamCmd projesi test projesinden referanslanmadığı için çıktısı kaynak
    /// ağacındaki bin klasöründen bulunur.
    /// </summary>
    private static string LocateBuildOutput([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "FakeSteamCmd", "bin");

            if (Directory.Exists(candidate))
            {
                var newest = Directory
                    .EnumerateFiles(candidate, "FakeSteamCmd.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest is not null)
                {
                    return Path.GetDirectoryName(newest)!;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "FakeSteamCmd derleme çıktısı bulunamadı. Önce çözümü derleyin.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Windows dosyayı hâlâ tutuyor olabilir; geçici klasör zaten temizlenecek.
        }
    }
}
