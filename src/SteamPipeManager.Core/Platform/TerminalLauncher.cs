using System.Diagnostics;

namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Linux ve macOS'ta bir betiği yeni bir terminal penceresinde çalıştırır.
///
/// macOS'ta <c>.command</c> uzantılı betik <c>open</c> ile Terminal'de açılıyor.
/// Linux'ta standart bir terminal yok; yaygın olanlar sırayla deneniyor.
/// Dönen süreç terminalin kendisi değil, onu başlatan komut olabilir — beklemek
/// için kullanılmamalı.
/// </summary>
public static class TerminalLauncher
{
    /// <summary>Linux'ta denenen terminaller ve betiği çalıştırma biçimleri.</summary>
    internal static readonly (string Program, string[] Arguments)[] LinuxTerminals =
    [
        ("x-terminal-emulator", ["-e"]),
        ("gnome-terminal", ["--"]),
        ("konsole", ["-e"]),
        ("xfce4-terminal", ["-x"]),
        ("kitty", []),
        ("alacritty", ["-e"]),
        ("xterm", ["-e"]),
    ];

    public static Process Open(string scriptPath, HostPlatform platform)
    {
        if (platform.IsMac)
        {
            return Start("open", ["-a", "Terminal", scriptPath])
                   ?? throw new InvalidOperationException("Terminal could not be opened.");
        }

        foreach (var (program, arguments) in LinuxTerminals)
        {
            if (FindOnPath(program) is not { } executable)
            {
                continue;
            }

            if (Start(executable, [.. arguments, "sh", scriptPath]) is { } process)
            {
                return process;
            }
        }

        throw new InvalidOperationException(
            "No terminal emulator was found (tried: " +
            string.Join(", ", LinuxTerminals.Select(t => t.Program)) + ").");
    }

    private static Process? Start(string program, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static string? FindOnPath(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, program);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
