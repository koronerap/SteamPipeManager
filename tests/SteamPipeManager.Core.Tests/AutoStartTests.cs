using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Widget'ın sistemle başlaması. macOS ve Linux dosyalarla, sahte bir ev dizininde
/// sınanıyor; Windows'un "Run" kaydına testlerde yazılmıyor (kullanıcının gerçek kaydı).
/// </summary>
public sealed class AutoStartTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"spm_autostart_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void On_linux_an_autostart_entry_is_written_and_removed()
    {
        var autoStart = new AutoStart(HostPlatform.LinuxX64, "PipeManagerPulse", _home);

        autoStart.Enable("/home/deck/Apps/Pipe Manager Hub/PipeManagerHub", ["--widget"], "Pipe Manager Pulse");

        Assert.True(autoStart.IsEnabled());
        Assert.Equal(Path.Combine(_home, ".config", "autostart", "pipemanagerpulse.desktop"), autoStart.LinuxDesktopPath);

        var entry = File.ReadAllText(autoStart.LinuxDesktopPath);
        Assert.Contains("Exec=\"/home/deck/Apps/Pipe Manager Hub/PipeManagerHub\" --widget", entry);
        Assert.Contains("Name=Pipe Manager Pulse", entry);

        autoStart.Disable();
        Assert.False(autoStart.IsEnabled());
    }

    [Fact]
    public void On_macos_a_launch_agent_runs_the_app_at_login()
    {
        var autoStart = new AutoStart(HostPlatform.MacArm64, "PipeManagerPulse", _home);

        autoStart.Enable("/Applications/Pipe Manager Hub.app/Contents/MacOS/PipeManagerHub", ["--widget"], "Pipe Manager Pulse");

        var plist = File.ReadAllText(autoStart.MacAgentPath);
        Assert.EndsWith(Path.Combine("Library", "LaunchAgents", "io.github.koronerap.pipemanagerpulse.plist"), autoStart.MacAgentPath);
        Assert.Contains("<string>/Applications/Pipe Manager Hub.app/Contents/MacOS/PipeManagerHub</string>", plist);
        Assert.Contains("<string>--widget</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);

        // Geçerli XML olmalı; launchd bozuk dosyayı sessizce yok sayıyor.
        System.Xml.Linq.XDocument.Parse(plist[plist.IndexOf("<plist", StringComparison.Ordinal)..]);

        autoStart.Disable();
        Assert.False(autoStart.IsEnabled());
    }

    [Fact]
    public void Paths_with_special_characters_are_quoted_for_each_format()
    {
        Assert.Equal("\"C:\\Program Files\\Pulse\\PipeManagerPulse.exe\" --widget",
            AutoStart.WindowsCommandLine(@"C:\Program Files\Pulse\PipeManagerPulse.exe", ["--widget"]));

        var entry = AutoStart.LinuxDesktopEntry("/opt/a $b/run", [], "x");
        Assert.Contains("Exec=\"/opt/a \\$b/run\"", entry);
    }
}
