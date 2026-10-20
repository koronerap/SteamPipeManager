using SteamPipeManager.Core.Updates;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Ana uygulama ile widget arasındaki işaretler. Güncelleme dosyaları değiştirmeden önce
/// açık widget'ı kapatıyor ve yeniden başlatılması için bunu hatırlıyor.
/// </summary>
public sealed class WidgetSignalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_signals_{Guid.NewGuid():N}");

    private WorkspaceLayout Layout => new(_root);

    public WidgetSignalsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileStream HoldLock() =>
        new(Path.Combine(_root, WidgetSignals.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void The_widget_counts_as_running_only_while_it_holds_the_lock()
    {
        Assert.False(WidgetSignals.IsRunning(Layout));

        using (HoldLock())
        {
            Assert.True(WidgetSignals.IsRunning(Layout));
        }

        // Kapanan widget'ın bıraktığı dosya "açık" sayılmıyor.
        Assert.True(File.Exists(Path.Combine(_root, WidgetSignals.LockFile)));
        Assert.False(WidgetSignals.IsRunning(Layout));
    }

    [Fact]
    public async Task Stopping_asks_the_widget_to_quit_and_waits_for_it()
    {
        var widgetLock = HoldLock();
        var quitFile = Path.Combine(_root, WidgetSignals.QuitRequestFile);

        // Sahte widget: kapan isteği gelince kilidi bırakıyor.
        var widget = Task.Run(async () =>
        {
            while (!File.Exists(quitFile))
            {
                await Task.Delay(50);
            }

            await Task.Delay(300);
            widgetLock.Dispose();
        });

        var wasRunning = await WidgetSignals.StopAsync(Layout, TimeSpan.FromSeconds(10));
        await widget;

        Assert.True(wasRunning);
        Assert.False(WidgetSignals.IsRunning(Layout));
    }

    [Fact]
    public async Task Stopping_when_no_widget_runs_does_nothing()
    {
        Assert.False(await WidgetSignals.StopAsync(Layout, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(Path.Combine(_root, WidgetSignals.QuitRequestFile)));
    }

    [Fact]
    public void The_restarted_app_is_told_to_reopen_the_widget()
    {
        var text = UpdateLaunchArguments.Build(new AppVersion(1, 2, 0), 77, restartWidget: true);
        var parsed = UpdateLaunchArguments.Parse(text.Split(' '));

        Assert.True(parsed.RestartWidget);
        Assert.Equal(77, parsed.WaitForProcessId);
        Assert.False(UpdateLaunchArguments.Parse(UpdateLaunchArguments.Build(new AppVersion(1, 2, 0), 77).Split(' ')).RestartWidget);
    }
}
