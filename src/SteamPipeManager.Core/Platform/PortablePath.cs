namespace SteamPipeManager.Core.Platform;

/// <summary>
/// Başka bir işletim sisteminde yazılmış yollar. ContentBuilder script'leri genellikle
/// Windows'ta yazılıyor (<c>D:\sdk\tools\ContentBuilder\scripts\depot_1.vdf</c>);
/// Linux ve macOS'ta <see cref="Path.GetFileName(string)"/> ters eğik çizgiyi ayırıcı
/// saymadığı için bu yoldan dosya adını çıkaramıyordu.
/// </summary>
public static class PortablePath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Son yol parçası; iki ayırıcıdan hangisi kullanılmış olursa olsun.</summary>
    public static string LeafName(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var index = trimmed.LastIndexOfAny(Separators);

        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>
    /// Göreli bir yolu bu işletim sisteminin ayırıcısına çevirir. Windows'ta iki ayırıcı
    /// da zaten geçerli; Linux ve macOS'ta ters eğik çizgi dosya adının parçası sayılırdı.
    /// </summary>
    public static string ToLocal(string path) =>
        OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');
}
