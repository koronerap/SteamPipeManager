using System.IO.Compression;

namespace SteamPipeManager.Core.Updates;

public enum InstallReadiness
{
    Ready,

    /// <summary>
    /// Uygulama bir yayın paketinden başlatılmamış (ör. <c>dotnet run</c> ile
    /// geliştirme çıktısı). Oraya paket açmak geliştirme klasörünü bozardı.
    /// </summary>
    DevelopmentBuild,

    /// <summary>Uygulama klasörüne yazılamıyor (ör. Program Files).</summary>
    NotWritable,
}

/// <summary>
/// Paketi açar ve çalışan uygulamanın dosyalarını yenileriyle değiştirir.
///
/// Windows çalışan bir exe'nin ya da yüklü bir DLL'in üzerine yazmaya izin vermiyor,
/// ama <b>adını değiştirmeye</b> izin veriyor. Bu yüzden her dosya önce
/// <c>.spm-old</c> ekiyle kenara alınıyor, yenisi yerine kopyalanıyor. Herhangi bir
/// adım başarısız olursa yapılanlar ters sırayla geri alınıyor ve uygulama
/// eski hâliyle çalışmaya devam ediyor. Kenara alınan dosyalar yeni sürüm
/// açıldığında siliniyor.
/// </summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".spm-old";

    public static InstallReadiness CheckReadiness(string? runningExecutable, string expectedExecutableName)
    {
        if (runningExecutable is not { Length: > 0 } ||
            !string.Equals(Path.GetFileName(runningExecutable), expectedExecutableName, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(runningExecutable) is not { Length: > 0 } directory)
        {
            return InstallReadiness.DevelopmentBuild;
        }

        var probe = Path.Combine(directory, $".spm-write-test-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return InstallReadiness.Ready;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InstallReadiness.NotWritable;
        }
    }

    /// <summary>
    /// Paketi hazırlık klasörüne açar ve uygulama dosyalarının bulunduğu kökü döndürür.
    /// Yayın zip'lerinde dosyalar ürün adıyla bir klasörün içinde duruyor; doğrudan
    /// kökte duran paketler de kabul ediliyor.
    /// </summary>
    public static string Stage(string packagePath, string stagingDirectory, string executableName)
    {
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);

        var root = Path.GetFullPath(stagingDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);

            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // Zip içindeki "..\..\" gibi adlar paketi hazırlık klasörünün dışına
                // yazdırmasın.
                if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(UpdateFailure.PackageInvalid, $"Entry escapes the package: {entry.FullName}");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException(UpdateFailure.PackageInvalid, ex.Message, ex);
        }

        if (File.Exists(Path.Combine(root, executableName)))
        {
            return root;
        }

        var folders = Directory.GetDirectories(root);
        var files = Directory.GetFiles(root);

        if (folders.Length == 1 && files.Length == 0 && File.Exists(Path.Combine(folders[0], executableName)))
        {
            return folders[0];
        }

        throw new UpdateException(UpdateFailure.PackageInvalid, $"{executableName} is not in the package.");
    }

    /// <summary>
    /// Hazırlanan dosyaları uygulama klasörüne yerleştirir. Başarısız olursa her şey
    /// geri alınmış olarak <see cref="UpdateException"/> fırlatır.
    ///
    /// Dönen işlem, yeni sürüm başlatılamazsa geri almak için tutuluyor.
    /// </summary>
    public static InstalledUpdate Install(string stagedRoot, string appDirectory)
    {
        var steps = new List<SwapStep>();

        try
        {
            foreach (var source in Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagedRoot, source);
                var target = Path.Combine(appDirectory, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string? backup = null;

                if (File.Exists(target))
                {
                    backup = target + OldSuffix;
                    ClearBackupSlot(backup);
                    File.Move(target, backup);
                }

                // Kopyadan önce kaydediliyor: kopya yarıda kalırsa geri alma yarım
                // dosyayı da temizlemeli.
                steps.Add(new SwapStep(target, backup));
                File.Copy(source, target, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = Rollback(steps);

            throw new UpdateException(
                UpdateFailure.InstallFailed,
                restored ? ex.Message : $"{ex.Message} (rollback incomplete)",
                ex);
        }

        return new InstalledUpdate(steps);
    }

    /// <summary>
    /// Önceki güncellemelerden kalan <c>.spm-old</c> dosyalarını siler. Kilitli olanlar
    /// (eski süreç henüz kapanmadıysa) sonraki açılışa kalır. Kalan dosya sayısını döndürür.
    /// </summary>
    public static int CleanupLeftovers(string appDirectory)
    {
        var remaining = 0;

        if (!Directory.Exists(appDirectory))
        {
            return 0;
        }

        foreach (var file in Directory.EnumerateFiles(appDirectory, "*" + OldSuffix + "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining++;
            }
        }

        return remaining;
    }

    /// <summary>
    /// Yedek adı doluysa — önceki bir güncellemenin silinemeyen artığı — önce silmeyi
    /// dener, olmazsa benzersiz bir adla kenara çeker.
    /// </summary>
    private static void ClearBackupSlot(string backup)
    {
        if (!File.Exists(backup))
        {
            return;
        }

        try
        {
            File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(backup, $"{backup}.{Guid.NewGuid():N}");
        }
    }

    internal static bool Rollback(IReadOnlyList<SwapStep> steps)
    {
        var complete = true;

        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var (target, backup) = steps[i];

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        return complete;
    }

    internal readonly record struct SwapStep(string Target, string? Backup);
}

/// <summary>Yerleştirilmiş bir güncelleme; yeni sürüm açılamazsa geri alınabilir.</summary>
public sealed class InstalledUpdate
{
    private readonly IReadOnlyList<UpdateInstaller.SwapStep> _steps;

    internal InstalledUpdate(IReadOnlyList<UpdateInstaller.SwapStep> steps) => _steps = steps;

    public int FileCount => _steps.Count;

    /// <summary>Eski dosyaları geri koyar. Tamamı geri konduysa true.</summary>
    public bool Rollback() => UpdateInstaller.Rollback(_steps);
}
