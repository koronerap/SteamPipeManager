namespace SteamPipeManager.Core.Storage;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Profillerden bağımsız, makine düzeyindeki ayarlar (<c>settings.json</c>).</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Kullanılacak <c>steamcmd.exe</c>. Null ise uygulamanın kendi indirdiği kurulum
    /// (<see cref="Workspace.WorkspaceLayout.ManagedSteamCmdDirectory"/>) kullanılır.
    /// </summary>
    public string? SteamCmdPath { get; set; }

    /// <summary>
    /// Kullanıcının BuildPatchTool kopyasının yolu. SteamCMD'nin aksine biz
    /// indiremiyoruz — Epic Dev Portal'ın arkasında ve dağıtım hakkımız yok — bu yüzden
    /// Epic profilleri için kullanıcının göstermesi gerekiyor.
    /// </summary>
    public string? BuildPatchToolPath { get; set; }

    /// <summary>İlk çalıştırma sihirbazı tamamlandı mı.</summary>
    public bool SetupCompleted { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Seçili dil kodu. Null ise ilk çalıştırma sayılır ve sistem diline göre belirlenir;
    /// eşleşme yoksa İngilizceye düşülür.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Açılışta doğrudan bu profile gitmek için; profil silinirse yok sayılır.</summary>
    public Guid? LastProfileId { get; set; }

    /// <summary>Klasör seçme diyaloglarının başlayacağı yer.</summary>
    public string? LastContentBrowseDirectory { get; set; }

    /// <summary>
    /// Build sırasında <c>console_log.txt</c> bu kadar saniye büyümezse donma uyarısı gösterilir
    /// (M0 Bulgu 4/5 gereği).
    /// </summary>
    public int StallWarningSeconds { get; set; } = 120;

    /// <summary>
    /// Açılışta GitHub'daki son sürüme bakılsın mı. Kurulum her zaman kullanıcının
    /// onayıyla yapılıyor; bu yalnızca soruyu soruyor.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Build öncesi oturum kontrolünün zaman aşımı.</summary>
    public int SessionCheckTimeoutSeconds { get; set; } = 45;
}
