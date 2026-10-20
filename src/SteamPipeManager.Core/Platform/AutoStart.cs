using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Oturum açılınca bir programı başlatma: Windows'ta kullanıcının "Run" kaydı, macOS'ta
/// LaunchAgent, Linux'ta XDG autostart dosyası (GNOME, KDE ve Steam Deck'in masaüstü
/// modu okuyor). Yalnızca kullanıcının kendi alanına yazılıyor; yönetici izni gerekmiyor.
/// </summary>
public sealed class AutoStart(HostPlatform platform, string name, string? homeDirectory = null)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private string Home => homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>macOS: <c>~/Library/LaunchAgents/io.github.koronerap.&lt;ad&gt;.plist</c>.</summary>
    public string MacAgentPath =>
        Path.Combine(Home, "Library", "LaunchAgents", $"io.github.koronerap.{name.ToLowerInvariant()}.plist");

    /// <summary>Linux: <c>$XDG_CONFIG_HOME/autostart/&lt;ad&gt;.desktop</c> (yoksa <c>~/.config</c>).</summary>
    public string LinuxDesktopPath
    {
        get
        {
            var config = homeDirectory is null && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".config");

            return Path.Combine(config, "autostart", $"{name.ToLowerInvariant()}.desktop");
        }
    }

    public bool IsEnabled() => platform.Os switch
    {
        HostOs.Windows when OperatingSystem.IsWindows() => ReadRunValue() is not null,
        HostOs.MacOS => File.Exists(MacAgentPath),
        HostOs.Linux => File.Exists(LinuxDesktopPath),
        _ => false,
    };

    public void Enable(string executable, IReadOnlyList<string> arguments, string displayName)
    {
        switch (platform.Os)
        {
            case HostOs.Windows when OperatingSystem.IsWindows():
                WriteRunValue(WindowsCommandLine(executable, arguments));
                break;

            case HostOs.MacOS:
                Write(MacAgentPath, MacAgent(executable, arguments));
                break;

            case HostOs.Linux:
                Write(LinuxDesktopPath, LinuxDesktopEntry(executable, arguments, displayName));
                break;
        }
    }

    public void Disable()
    {
        switch (platform.Os)
        {
            case HostOs.Windows when OperatingSystem.IsWindows():
                DeleteRunValue();
                break;

            case HostOs.MacOS:
                File.Delete(MacAgentPath);
                break;

            case HostOs.Linux:
                File.Delete(LinuxDesktopPath);
                break;
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Boşluk içeren yollar tırnakla; "Run" kaydı komut satırını olduğu gibi çalıştırıyor.</summary>
    internal static string WindowsCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    internal string MacAgent(string executable, IReadOnlyList<string> arguments)
    {
        var items = string.Concat(new[] { executable }.Concat(arguments)
            .Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>io.github.koronerap.{name.ToLowerInvariant()}</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>
    /// Desktop Entry'de Exec alanı kendi kurallarıyla tırnaklanıyor: boşluk içeren yol
    /// çift tırnak içinde, içindeki <c>"</c>, <c>`</c>, <c>$</c> ve <c>\</c> kaçışlı.
    /// </summary>
    internal static string LinuxDesktopEntry(string executable, IReadOnlyList<string> arguments, string displayName)
    {
        static string Quote(string value) =>
            value.IndexOfAny([' ', '"', '\'', '\\', '$', '`', '\t']) < 0
                ? value
                : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";

        var exec = string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));

        return $"""
            [Desktop Entry]
            Type=Application
            Name={displayName}
            Exec={exec}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
    }

    [SupportedOSPlatform("windows")]
    private string? ReadRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    [SupportedOSPlatform("windows")]
    private void WriteRunValue(string commandLine)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, commandLine);
    }

    [SupportedOSPlatform("windows")]
    private void DeleteRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
