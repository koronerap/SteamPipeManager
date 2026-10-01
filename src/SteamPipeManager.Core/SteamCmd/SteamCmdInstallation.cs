using System.Diagnostics;
using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// Diskteki bir SteamCMD kurulumu. Yolların tek yerden türetilmesini sağlar;
/// özellikle canlı logun okunacağı <c>logs/console_log.txt</c>.
///
/// Linux ve macOS'ta log ve oturum önbelleği kurulum klasöründe değil, ev dizininde
/// duruyor (bkz. <see cref="SteamCmdLayout"/>). Orada SteamCMD'ye uygulamanın kendi
/// ev dizini veriliyor; <see cref="Prepare"/> bunu her süreç için ayarlıyor.
/// </summary>
public sealed class SteamCmdInstallation
{
    /// <summary>SteamCMD güncellendiğinde bu kodla çıkar; hata değil, "yeniden başlat" demektir.</summary>
    public const int RestartRequiredExitCode = 7;

    /// <param name="homeDirectory">
    /// Linux/macOS'ta SteamCMD'ye verilecek ev dizini. Verilmezse kurulumun yanındaki
    /// <c>home</c> klasörü. Windows'ta kullanılmıyor.
    /// </param>
    public SteamCmdInstallation(string executablePath, HostPlatform? platform = null, string? homeDirectory = null)
    {
        ExecutablePath = executablePath;
        Platform = platform ?? HostPlatform.Current;
        HomeDirectory = Platform.IsWindows
            ? null
            : homeDirectory ?? Path.Combine(Directory, "home");
    }

    public string ExecutablePath { get; }

    public HostPlatform Platform { get; }

    public string Directory => Path.GetDirectoryName(ExecutablePath)!;

    /// <summary>Linux/macOS'ta SteamCMD'nin <c>HOME</c>'u; Windows'ta null.</summary>
    public string? HomeDirectory { get; }

    /// <summary>Log ve oturum önbelleğinin kökü.</summary>
    public string DataDirectory =>
        SteamCmdLayout.DataDirectory(Platform, Directory, HomeDirectory ?? Directory);

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>M0'da canlı yazıldığı doğrulanan dosya (Windows).</summary>
    public string ConsoleLogPath => Path.Combine(LogsDirectory, "console_log.txt");

    /// <summary>
    /// Canlı ilerlemenin nereden okunacağı. Windows'ta stdout boru üzerinden tamponlanıyor
    /// ve ancak süreç bitince geliyor, canlı kaynak <c>console_log.txt</c> (M0 ölçümleri).
    /// Linux'ta tam tersi: <c>console_log.txt</c> hiç yazılmıyor, stdout ise satır satır
    /// geliyor (WSL'de Ubuntu 24.04, SteamCMD 1788292693 ile ölçüldü). macOS Linux gibi
    /// varsayılıyor; CI'da ölçülüyor.
    /// </summary>
    public bool LiveOutputIsStandardOutput => !Platform.IsWindows;

    /// <summary>Oturum cache'i burada tutulur; profiller arası paylaşılır.</summary>
    public string ConfigPath => Path.Combine(DataDirectory, "config", "config.vdf");

    /// <summary>
    /// SteamCMD bu makinede çalışabilir mi. macOS SteamCMD'si yalnızca Intel ikilisi;
    /// Apple Silicon'da Rosetta yoksa "Bad CPU type in executable" ile düşüyor ve
    /// kullanıcı ne olduğunu anlamıyor. Ne yapacağını söyleyen hata burada.
    /// </summary>
    public void EnsureCanRun()
    {
        if (Platform.IsMac && Platform.Architecture == "arm64" && OperatingSystem.IsMacOS() && !Rosetta.IsInstalled())
        {
            throw new InvalidOperationException(
                "SteamCMD is an Intel program, and this Mac needs Rosetta to run it. " +
                $"Install Rosetta in Terminal with \"{Rosetta.InstallCommand}\", then try again.");
        }
    }

    /// <summary>
    /// SteamCMD süreci başlamadan önce: Linux/macOS'ta ev dizinini ayarlar ve betiğin
    /// çalıştırma iznini garanti eder. Windows'ta yalnızca çalışabilirliğe bakar.
    /// </summary>
    public void Prepare(ProcessStartInfo startInfo)
    {
        EnsureCanRun();

        if (HomeDirectory is not { } home)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(home);
        startInfo.Environment["HOME"] = home;

        UnixPermissions.EnsureExecutable(ExecutablePath);
    }

    public bool Exists => File.Exists(ExecutablePath);

    /// <summary>
    /// Yeni bir çalıştırma öncesi eski logu siler ki takip edilen dosya yalnızca
    /// bu çalıştırmanın çıktısını içersin.
    /// </summary>
    /// <summary>
    /// Yeni bir çalıştırma öncesi konsol logunu temizler.
    /// </summary>
    /// <returns>
    /// Takibin başlaması gereken bayt konumu. Dosya silinebildiyse 0; silinemediyse
    /// (ör. hâlâ kapanmakta olan bir SteamCMD süreci tutuyorsa) mevcut uzunluk —
    /// böylece başka bir hesaba ait eski satırlar bu çalıştırmaya karışmaz.
    /// </returns>
    public long ResetConsoleLog()
    {
        try
        {
            if (File.Exists(ConsoleLogPath))
            {
                File.Delete(ConsoleLogPath);
            }

            return 0;
        }
        catch (IOException)
        {
            // Silinemedi; içeriğini boşaltmayı dene.
        }

        try
        {
            using var stream = new FileStream(
                ConsoleLogPath, FileMode.Truncate, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            return 0;
        }
        catch (IOException)
        {
            // Boşaltılamadı; eski içerik atlanarak devam edilir.
        }

        try
        {
            return new FileInfo(ConsoleLogPath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
