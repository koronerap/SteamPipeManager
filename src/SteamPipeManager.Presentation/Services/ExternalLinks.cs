using System.ComponentModel;
using System.Diagnostics;

namespace SteamPipeManager.Presentation.Services;

/// <summary>Bağlantıyı sistemin tarayıcısında açar; açılamazsa sessizce geçer.</summary>
public static class ExternalLinks
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Tarayıcı yoksa (ör. masaüstü olmayan Linux) yapılacak bir şey yok.
        }
    }

    public static string SteamDbCharts(uint appId) => $"https://steamdb.info/app/{appId}/charts/";

    public static string SteamStore(uint appId) => $"https://store.steampowered.com/app/{appId}/";
}
