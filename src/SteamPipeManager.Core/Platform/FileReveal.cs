using System.Diagnostics;

namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Bir dosyayı sistemin dosya yöneticisinde gösterir.
///
/// Windows'ta Gezgin dosyayı seçili açıyor, macOS'ta Finder da (<c>open -R</c>).
/// Linux'ta ortak bir "seç" komutu yok; dosyanın bulunduğu klasör açılıyor.
/// </summary>
public static class FileReveal
{
    public static void Show(string path, HostPlatform? platform = null)
    {
        platform ??= HostPlatform.Current;

        var startInfo = platform.Os switch
        {
            HostOs.Windows => new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true },
            HostOs.MacOS => new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false },
            _ => new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path }, UseShellExecute = false },
        };

        try
        {
            using var _ = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Dosya yöneticisi yoksa (ör. başsız Linux) sessizce geçiliyor; yol arayüzde görünüyor.
        }
    }
}
