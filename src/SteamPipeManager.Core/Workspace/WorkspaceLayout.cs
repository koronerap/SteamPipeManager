using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Workspace;

/// <summary>
/// Uygulamanın diskteki klasör şeması. Her SubApp kendi <c>scripts/</c> ve <c>output/</c>
/// klasörünü alır; SteamCMD tek kopya olduğu için SDK'nın oyun başına kopyalanması gerekmez.
/// </summary>
public sealed class WorkspaceLayout(string rootDirectory)
{
    /// <summary>
    /// Platformun alışılmış yeri: Windows'ta <c>%AppData%</c> (1.0'dan beri), Linux'ta
    /// <c>~/.local/share</c> (XDG_DATA_HOME'a uyar), macOS'ta
    /// <c>~/Library/Application Support</c>. .NET'te son ikisi LocalApplicationData.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        "SteamPipeManager");

    /// <summary>
    /// Veri klasörünü değiştiren ortam değişkeni. Uygulamayı gerçek profillere
    /// dokunmadan, boş ya da hazırlanmış bir klasörle çalıştırıp sınamak için.
    /// </summary>
    public const string DataDirectoryVariable = "SPM_DATA_DIR";

    public static WorkspaceLayout Default() =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? new(Path.GetFullPath(custom))
            : new(DefaultRoot);

    public string Root { get; } = rootDirectory;

    public string ProfilesFile => Path.Combine(Root, "profiles.json");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Epic client secret'larının şifreli deposu. <c>profiles.json</c>'dan ayrı bir
    /// dosya, çünkü profil dosyasının paylaşılabilir kalması gerekiyor.
    /// </summary>
    public string EpicSecretsFile => Path.Combine(Root, "epic-secrets.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string CoversDirectory => Path.Combine(Root, "covers");

    /// <summary>Kullanıcının kendi dil dosyalarını bırakabildiği klasör.</summary>
    public string LanguageDirectory => Path.Combine(Root, "lang");

    /// <summary>İndirilen güncelleme paketleri ve açıldıkları hazırlık klasörü.</summary>
    public string UpdatesDirectory => Path.Combine(Root, "updates");

    /// <summary>
    /// Linux/macOS'ta SteamCMD'ye verilen ev dizini: log'u ve oturum önbelleği burada,
    /// kullanıcının gerçek ev dizininden ve Steam istemcisinden ayrı.
    /// </summary>
    public string SteamCmdHomeDirectory => Path.Combine(Root, "steamcmd-home");

    /// <summary>Uygulamanın kendi indirdiği SteamCMD kurulumu.</summary>
    public string ManagedSteamCmdDirectory => Path.Combine(Root, "steamcmd");

    public string SubAppDirectory(Guid profileId, Guid appId, Guid subAppId) =>
        Path.Combine(Root, "workspaces", profileId.ToString("N"), appId.ToString("N"), subAppId.ToString("N"));

    public string ScriptsDirectory(Guid profileId, Guid appId, Guid subAppId) =>
        Path.Combine(SubAppDirectory(profileId, appId, subAppId), "scripts");

    public string OutputDirectory(Guid profileId, Guid appId, Guid subAppId) =>
        Path.Combine(SubAppDirectory(profileId, appId, subAppId), "output");

    public string AppScriptPath(Guid profileId, Guid appId, SubApp subApp) =>
        Path.Combine(ScriptsDirectory(profileId, appId, subApp.Id), AppScriptFileName(subApp.SteamAppId));

    public string DepotScriptPath(Guid profileId, Guid appId, Guid subAppId, uint depotId) =>
        Path.Combine(ScriptsDirectory(profileId, appId, subAppId), DepotScriptFileName(depotId));

    /// <summary>Referans script'lerdeki adlandırma: <c>app_&lt;appid&gt;.vdf</c>.</summary>
    public static string AppScriptFileName(uint steamAppId) => $"app_{steamAppId}.vdf";

    /// <summary>Referans script'lerdeki adlandırma: <c>depot_&lt;depotid&gt;.vdf</c>.</summary>
    public static string DepotScriptFileName(uint depotId) => $"depot_{depotId}.vdf";

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CoversDirectory);
        Directory.CreateDirectory(LanguageDirectory);
    }
}
