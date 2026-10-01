using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Kullanıcının BuildPatchTool kurulumu.
///
/// SteamCMD'den önemli bir farkı var: BPT'yi biz indiremiyoruz. Epic Dev Portal'ın
/// arkasında ve dağıtım hakkımız yok, dolayısıyla kullanıcı kendi kopyasını göstermek
/// zorunda. Bu yüzden burada "kur/indir" değil yalnızca "bul ve doğrula" var.
/// </summary>
public sealed class BptInstallation
{
    public BptInstallation(string executablePath) => ExecutablePath = executablePath;

    public string ExecutablePath { get; }

    public bool Exists => File.Exists(ExecutablePath);

    /// <summary>
    /// Epic'in zip'i üç platformun ikilisini birden taşıyor; aranan, çalışılan
    /// platformunki. Windows'ta <c>.exe</c>, Linux ve macOS'ta uzantısız.
    /// </summary>
    public static string ExecutableName(HostPlatform platform) =>
        platform.IsWindows ? "BuildPatchTool.exe" : "BuildPatchTool";

    /// <summary>Zip açıldığında aracın bulunduğu göreli yol.</summary>
    public static string RelativePath(HostPlatform platform) => Path.Combine(
        "Engine",
        "Binaries",
        platform.Os switch
        {
            HostOs.Windows => "Win64",
            HostOs.MacOS => "Mac",
            _ => "Linux",
        },
        ExecutableName(platform));

    /// <summary>
    /// Kullanıcının gösterdiği yolda BuildPatchTool'u arar.
    ///
    /// Kabul edilenler: aracın kendisi, zip'in açıldığı kök klasör
    /// (<c>BuildPatchTool_1.8.8</c>) ve arada kalan klasörler. Kullanıcının
    /// hangisini seçeceğini bilmediğimiz için hepsi denenir.
    /// </summary>
    public static string? LocateExecutable(string path, HostPlatform? platform = null)
    {
        platform ??= HostPlatform.Current;

        if (path is not { Length: > 0 })
        {
            return null;
        }

        var name = ExecutableName(platform);
        var relative = RelativePath(platform);

        if (File.Exists(path))
        {
            // Linux/macOS'ta ad büyük/küçük harfe duyarlı; Windows'ta değil.
            var comparison = platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return string.Equals(Path.GetFileName(path), name, comparison) ? path : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(path, relative),
            Path.Combine(path, name),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Zip bir alt klasöre açılmış olabilir (ör. seçilen klasörün içinde
        // BuildPatchTool_1.8.8 duruyordur).
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                var nested = Path.Combine(directory, relative);

                if (File.Exists(nested))
                {
                    return nested;
                }
            }
        }
        catch (IOException)
        {
            // Okunamayan klasör atlanır.
        }

        return null;
    }

    /// <summary>
    /// Sürümü klasör adından tahmin eder (<c>BuildPatchTool_1.8.8</c>). Kesin sürüm
    /// aracın kendi çıktısında (<c>BUILDPATCHTOOL v1.8.8-…</c>) ama onu almak için
    /// süreç başlatmak gerekiyor; bu ucuz tahmin arayüzde göstermeye yetiyor.
    /// </summary>
    public string? VersionFromPath()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(ExecutablePath) ?? "");

        while (directory is not null)
        {
            if (directory.Name.StartsWith("BuildPatchTool_", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Name["BuildPatchTool_".Length..];
            }

            directory = directory.Parent;
        }

        return null;
    }
}
