using System.ComponentModel;
using System.Diagnostics;

namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Apple Silicon'da Intel programlarını çalıştıran çevirmen. Kurulu değilse Intel
/// ikilileri "Bad CPU type in executable" hatasıyla açılmıyor; macOS SteamCMD'si de
/// yalnızca x86_64 (Valve'ın paketi incelendi).
/// </summary>
public static class Rosetta
{
    public const string InstallCommand = "softwareupdate --install-rosetta --agree-to-license";

    private static bool _installed;

    /// <summary>
    /// Bir Intel programı gerçekten çalıştırılarak bakılıyor: sistemin evrensel
    /// <c>/usr/bin/true</c>'su x86_64 olarak. Dosya yolu denetimleri macOS sürümleri
    /// arasında değişiyor. Kurulu bulunursa sonuç saklanıyor; bulunmazsa her seferinde
    /// yeniden bakılıyor ki kullanıcı kurduktan sonra uygulamayı yeniden başlatmak
    /// zorunda kalmasın.
    /// </summary>
    public static bool IsInstalled()
    {
        if (_installed)
        {
            return true;
        }

        var startInfo = new ProcessStartInfo("/usr/bin/arch")
        {
            ArgumentList = { "-x86_64", "/usr/bin/true" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var probe = Process.Start(startInfo);

            if (probe is null)
            {
                return false;
            }

            if (!probe.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                // Çeviri sürüyorsa Rosetta var demektir; asıl çalıştırma gerisini gösterir.
                probe.Kill();
                return true;
            }

            _installed = probe.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }

        return _installed;
    }
}
