namespace SteamPipeManager.Core.Workspace;

/// <summary>
/// Ana uygulama ile widget (ayrı süreç) arasındaki işaretler, veri klasöründeki
/// dosyalarla: widget açıkken bir kilit dosyası tutuyor; "görün" ve "kapan" istekleri
/// birer dosya. Platformdan bağımsız ve ek bağımlılık istemiyor.
/// </summary>
public static class WidgetSignals
{
    /// <summary>Widget açık olduğu sürece paylaşımsız tutulan dosya.</summary>
    public const string LockFile = "widget.lock";

    /// <summary>Açık widget'a "şeridi göster": ikinci kopya ya da ana uygulamadaki düğme.</summary>
    public const string ShowRequestFile = "widget.show";

    /// <summary>
    /// Açık widget'a "kapan": güncelleme dosyaları değiştirmeden önce. Windows'ta çalışan
    /// dosyanın bulunduğu klasör taşınamıyor; diğer platformlarda widget eski sürümle
    /// çalışmaya devam ederdi.
    /// </summary>
    public const string QuitRequestFile = "widget.quit";

    public static bool IsRunning(WorkspaceLayout layout)
    {
        var path = Path.Combine(layout.Root, LockFile);

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Request(WorkspaceLayout layout, string requestFile)
    {
        try
        {
            Directory.CreateDirectory(layout.Root);
            File.WriteAllText(Path.Combine(layout.Root, requestFile), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // İstek iletilemedi; widget olduğu gibi kalıyor.
        }
    }

    /// <summary>
    /// Açık widget'ı kapatır ve kapanmasını bekler. Açıktıysa true: güncellemeden sonra
    /// yeniden başlatılsın diye.
    /// </summary>
    public static async Task<bool> StopAsync(WorkspaceLayout layout, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsRunning(layout))
        {
            return false;
        }

        Request(layout, QuitRequestFile);

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (IsRunning(layout) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        return true;
    }
}
