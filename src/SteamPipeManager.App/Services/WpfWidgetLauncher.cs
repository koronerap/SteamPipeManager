using System.Diagnostics;
using System.IO;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.App.Services;

/// <summary>
/// Windows'ta widget ayrı bir dosya (Avalonia ile derlenmiş PipeManagerPulse.exe) ve
/// yayın paketinde uygulamanın yanında duruyor. Geliştirme derlemesinde yoksa false.
/// </summary>
internal sealed class WpfWidgetLauncher(WorkspaceLayout layout) : IWidgetLauncher
{
    public const string FileName = "PipeManagerPulse.exe";

    public bool Launch()
    {
        // Zaten açıksa yeni süreç başlatmadan şeridi geri getir.
        if (WidgetSignals.IsRunning(layout))
        {
            WidgetSignals.Request(layout, WidgetSignals.ShowRequestFile);
            return true;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, FileName);

        if (!File.Exists(exe))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        return true;
    }
}
