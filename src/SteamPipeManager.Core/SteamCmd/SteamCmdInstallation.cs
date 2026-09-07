namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// Diskteki bir SteamCMD kurulumu. Yolların tek yerden türetilmesini sağlar;
/// özellikle canlı logun okunacağı <c>logs/console_log.txt</c>.
/// </summary>
public sealed class SteamCmdInstallation(string executablePath)
{
    /// <summary>SteamCMD güncellendiğinde bu kodla çıkar; hata değil, "yeniden başlat" demektir.</summary>
    public const int RestartRequiredExitCode = 7;

    public string ExecutablePath { get; } = executablePath;

    public string Directory => Path.GetDirectoryName(ExecutablePath)!;

    public string LogsDirectory => Path.Combine(Directory, "logs");

    /// <summary>M0'da canlı yazıldığı doğrulanan dosya.</summary>
    public string ConsoleLogPath => Path.Combine(LogsDirectory, "console_log.txt");

    /// <summary>Oturum cache'i burada tutulur; profiller arası paylaşılır.</summary>
    public string ConfigPath => Path.Combine(Directory, "config", "config.vdf");

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
