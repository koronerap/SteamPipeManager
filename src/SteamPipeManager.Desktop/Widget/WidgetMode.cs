using System.Diagnostics;
using System.Reflection;
using SteamPipeManager.Core.Platform;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Widget olarak mı çalışılıyor. Linux ve macOS'ta ana uygulamanın kendisi
/// <c>--widget</c> ile açılıyor (paket büyümüyor). Windows'ta ana uygulama WPF; widget
/// bu projenin ayrı bir derlemesi (<c>PipeManagerPulse.exe</c>) ve varsayılan olarak
/// widget açılıyor.
/// </summary>
internal static class WidgetMode
{
    public const string Argument = "--widget";

    /// <summary>Widget olarak derlenmiş çalıştırılabilirde ana uygulamayı açmak için.</summary>
    public const string AppArgument = "--app";

    public const string DisplayName = "Pipe Manager Pulse";

    /// <summary>Sistemle başlatma kaydının adı ve Windows'taki çalıştırılabilirin adı.</summary>
    public const string ExecutableName = "PipeManagerPulse";

    public static bool IsBuiltAsWidget { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "SpmWidget" && a.Value == "true");

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Contains(Argument) || (IsBuiltAsWidget && !args.Contains(AppArgument));

    /// <summary>Oturum açılınca widget'ı başlatacak komut.</summary>
    public static (string Executable, string[] Arguments) StartCommand =>
        IsBuiltAsWidget ? (Environment.ProcessPath!, []) : (Environment.ProcessPath!, [Argument]);
}

/// <summary>
/// Aynı anda tek widget. İkincisi açılırsa birincisine "görün" der ve kapanır: ana
/// uygulamadaki "Widget'ı aç" düğmesi gizlenmiş şeridi de geri getirsin diye. Güncelleme
/// öncesinde ana uygulama "kapan" diyor (bkz. <see cref="WidgetSignals"/>).
/// </summary>
internal sealed class WidgetInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly FileSystemWatcher _watcher;
    private readonly string _directory;

    private WidgetInstance(FileStream fileLock, string directory)
    {
        _lock = fileLock;
        _directory = directory;

        // Önceki oturumdan kalmış istekler yok sayılıyor.
        Consume(WidgetSignals.ShowRequestFile);
        Consume(WidgetSignals.QuitRequestFile);

        _watcher = new FileSystemWatcher(directory, "widget.*") { EnableRaisingEvents = true };
        _watcher.Created += (_, e) => OnRequest(e.Name);
        _watcher.Changed += (_, e) => OnRequest(e.Name);
    }

    /// <summary>Başka bir kopya "görün" dedi; olay arka plan iş parçacığında geliyor.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Ana uygulama güncelleme için "kapan" dedi; arka plan iş parçacığında.</summary>
    public event EventHandler? QuitRequested;

    private void OnRequest(string? name)
    {
        if (string.Equals(name, WidgetSignals.ShowRequestFile, StringComparison.OrdinalIgnoreCase) &&
            Consume(WidgetSignals.ShowRequestFile))
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (string.Equals(name, WidgetSignals.QuitRequestFile, StringComparison.OrdinalIgnoreCase) &&
                 Consume(WidgetSignals.QuitRequestFile))
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>İstek dosyasını siler; vardıysa true. İzleyici aynı isteği iki kez bildirebiliyor.</summary>
    private bool Consume(string file)
    {
        try
        {
            var path = Path.Combine(_directory, file);

            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Kilit alınamazsa widget zaten açık: ona haber verilip null dönüyor.</summary>
    public static WidgetInstance? TryAcquire(WorkspaceLayout layout)
    {
        Directory.CreateDirectory(layout.Root);

        try
        {
            var stream = new FileStream(
                Path.Combine(layout.Root, WidgetSignals.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new WidgetInstance(stream, layout.Root);
        }
        catch (IOException)
        {
            WidgetSignals.Request(layout, WidgetSignals.ShowRequestFile);
            return null;
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// Avalonia arayüzünden (Linux, macOS) widget: aynı çalıştırılabilir <c>--widget</c> ile.
/// </summary>
internal sealed class AvaloniaWidgetLauncher(WorkspaceLayout layout) : IWidgetLauncher
{
    public bool Launch()
    {
        // Zaten açıksa yeni süreç başlatmadan şeridi geri getir.
        if (WidgetSignals.IsRunning(layout))
        {
            WidgetSignals.Request(layout, WidgetSignals.ShowRequestFile);
            return true;
        }

        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { WidgetMode.Argument }, UseShellExecute = false });
        return true;
    }
}

/// <summary>Widget'tan ana uygulamayı açma.</summary>
internal static class MainAppLauncher
{
    /// <summary>
    /// Windows'ta widget ayrı bir dosya; ana uygulama yanındaki ürün dosyası
    /// (<c>PipeManagerHub.exe</c> gibi). Diğer platformlarda aynı çalıştırılabilir.
    /// </summary>
    public static bool Launch()
    {
        string? exe;
        string[] arguments = [];

        if (WidgetMode.IsBuiltAsWidget)
        {
            exe = Path.Combine(AppContext.BaseDirectory, $"{App.CurrentProduct.Id}.exe");

            if (!File.Exists(exe))
            {
                // Geliştirme sırasında WPF yanında değil: kendini ana uygulama olarak aç.
                exe = Environment.ProcessPath;
                arguments = [WidgetMode.AppArgument];
            }
        }
        else
        {
            exe = Environment.ProcessPath;
        }

        if (exe is null || !File.Exists(exe))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
        return true;
    }
}

/// <summary>Sistemle başlatma; widget'ın tepsi menüsündeki seçenek.</summary>
internal static class WidgetAutoStart
{
    private static AutoStart AutoStart => new(HostPlatform.Current, WidgetMode.ExecutableName);

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return AutoStart.IsEnabled();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var (exe, arguments) = WidgetMode.StartCommand;
                AutoStart.Enable(exe, arguments, WidgetMode.DisplayName);
            }
            else
            {
                AutoStart.Disable();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Kayıt yazılamadı; menüdeki işaret gerçek durumu yeniden okuyor.
        }
    }
}
