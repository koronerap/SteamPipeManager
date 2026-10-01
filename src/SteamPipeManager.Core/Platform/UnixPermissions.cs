namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Linux ve macOS'ta çalıştırma izni. Zip ile taşınan dosyalar bu izni kaybedebiliyor;
/// izni olmayan bir araç "Permission denied" ile başlamaz.
/// </summary>
public static class UnixPermissions
{
    private const UnixFileMode Execute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Dosyaya çalıştırma izni ekler. Windows'ta hiçbir şey yapmaz.</summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);

            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(path, mode | Execute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // İzin verilemiyorsa araç başlarken anlaşılır bir hata verir.
        }
    }

    /// <summary>Dosyayı yalnızca sahibinin okuyup yazabileceği hâle getirir (0600).</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
