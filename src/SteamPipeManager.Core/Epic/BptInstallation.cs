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
    /// <summary>Zip açıldığında exe'nin bulunduğu göreli yol.</summary>
    private const string WindowsRelativePath = @"Engine\Binaries\Win64\BuildPatchTool.exe";

    public BptInstallation(string executablePath) => ExecutablePath = executablePath;

    public string ExecutablePath { get; }

    public bool Exists => File.Exists(ExecutablePath);

    /// <summary>
    /// Kullanıcının gösterdiği yolda BuildPatchTool'u arar.
    ///
    /// Kabul edilenler: exe'nin kendisi, zip'in açıldığı kök klasör
    /// (<c>BuildPatchTool_1.8.8\</c>) ve arada kalan klasörler. Kullanıcının
    /// hangisini seçeceğini bilmediğimiz için hepsi denenir.
    /// </summary>
    public static string? LocateExecutable(string path)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path.EndsWith("BuildPatchTool.exe", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(path, WindowsRelativePath),
            Path.Combine(path, "BuildPatchTool.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Zip bir alt klasöre açılmış olabilir (ör. seçilen klasörün içinde
        // BuildPatchTool_1.8.8\ duruyordur).
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                var nested = Path.Combine(directory, WindowsRelativePath);

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
