using System.Runtime.InteropServices;

namespace SteamPipeManager.Core.Platform;

public enum HostOs
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// Uygulamanın çalıştığı işletim sistemi ve işlemci mimarisi.
///
/// Platforma göre değişen her şey (araç adları, indirme adresleri, veri klasörü,
/// güncelleme paketinin adı) bu değerden türetiliyor. Testler başka bir platformu
/// taklit edebilsin diye bir değer olarak geçiriliyor; doğrudan
/// <see cref="OperatingSystem"/>'e bakan kod yalnızca <see cref="Detect"/>.
/// </summary>
public sealed record HostPlatform(HostOs Os, string Architecture)
{
    public static HostPlatform Current { get; } = Detect();

    public static HostPlatform WindowsX64 => new(HostOs.Windows, "x64");

    public static HostPlatform LinuxX64 => new(HostOs.Linux, "x64");

    public static HostPlatform MacArm64 => new(HostOs.MacOS, "arm64");

    public static HostPlatform MacX64 => new(HostOs.MacOS, "x64");

    public bool IsWindows => Os == HostOs.Windows;

    public bool IsMac => Os == HostOs.MacOS;

    public bool IsLinux => Os == HostOs.Linux;

    /// <summary>.NET çalışma zamanı kimliği: <c>win-x64</c>, <c>linux-x64</c>, <c>osx-arm64</c>.</summary>
    public string RuntimeId => $"{OsMoniker}-{Architecture}";

    private string OsMoniker => Os switch
    {
        HostOs.Windows => "win",
        HostOs.Linux => "linux",
        _ => "osx",
    };

    public static HostPlatform Detect()
    {
        var os = OperatingSystem.IsWindows() ? HostOs.Windows
            : OperatingSystem.IsMacOS() ? HostOs.MacOS
            : HostOs.Linux;

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x64",
        };

        // Rosetta altında x64 bir yapı çalışıyorsa güncellemeler de x64 olmalı:
        // önemli olan makine değil, bu sürecin mimarisi.
        if (os == HostOs.MacOS)
        {
            architecture = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "x64";
        }

        return new HostPlatform(os, architecture);
    }
}
