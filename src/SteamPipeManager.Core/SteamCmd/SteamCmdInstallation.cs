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
    public void ResetConsoleLog()
    {
        try
        {
            if (File.Exists(ConsoleLogPath))
            {
                File.Delete(ConsoleLogPath);
            }
        }
        catch (IOException)
        {
            // Silinemezse takip zaten küçülme/başlangıç tespitiyle baş edebiliyor.
        }
    }
}
