using SteamPipeManager.Core.Platform;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// SteamCMD'nin her platformdaki dosya düzeni.
///
/// Windows'ta her şey kurulum klasöründe: <c>steamcmd.exe</c>, <c>logs/</c>, <c>config/</c>.
/// Linux ve macOS'ta Valve'ın dağıtımı farklı: kurulum klasöründe bir kabuk betiği
/// (<c>steamcmd.sh</c>) duruyor, loglar ve oturum önbelleği ise kullanıcının ev dizinine
/// yazılıyor — Linux'ta <c>~/Steam</c>, macOS'ta <c>~/Library/Application Support/Steam</c>.
///
/// macOS'taki yol Steam istemcisininkiyle aynı; istemci açıksa onun log'u ile
/// karışırdı. Bu yüzden Linux ve macOS'ta SteamCMD'ye uygulamanın kendi ev dizini
/// veriliyor (<c>HOME</c>): log yolu her zaman belli ve istemciden ayrı.
/// </summary>
public static class SteamCmdLayout
{
    private const string CdnBase = "https://steamcdn-a.akamaihd.net/client/installer/";

    public static string ExecutableName(HostPlatform platform) =>
        platform.IsWindows ? "steamcmd.exe" : "steamcmd.sh";

    public static Uri DownloadUrl(HostPlatform platform) => new(CdnBase + platform.Os switch
    {
        HostOs.Windows => "steamcmd.zip",
        HostOs.MacOS => "steamcmd_osx.tar.gz",
        _ => "steamcmd_linux.tar.gz",
    });

    /// <summary>
    /// Steamworks SDK'daki ContentBuilder'da her platformun kendi builder klasörü var;
    /// kullanıcı SDK'yı gösterirse çalıştığı platformunki aranıyor.
    /// </summary>
    public static string BuilderFolder(HostPlatform platform) => platform.Os switch
    {
        HostOs.Windows => "builder",
        HostOs.MacOS => "builder_osx",
        _ => "builder_linux",
    };

    /// <summary>
    /// Log ve oturum önbelleğinin bulunduğu kök. Windows'ta kurulum klasörü; Linux ve
    /// macOS'ta <paramref name="home"/> altındaki Steam klasörü.
    /// </summary>
    public static string DataDirectory(HostPlatform platform, string installDirectory, string home) =>
        platform.Os switch
        {
            HostOs.Windows => installDirectory,
            HostOs.MacOS => Path.Combine(home, "Library", "Application Support", "Steam"),
            _ => Path.Combine(home, "Steam"),
        };
}
