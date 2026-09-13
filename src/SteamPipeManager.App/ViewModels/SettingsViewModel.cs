using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.App.ViewModels;

/// <summary>
/// Makine düzeyindeki ayarlar: SteamCMD kurulumu, tema ve zaman aşımı süreleri.
/// </summary>
public sealed partial class SettingsViewModel(
    ISettingsStore store,
    WorkspaceLayout layout,
    BuildCoordinator coordinator,
    IDialogService dialogs,
    ProductProfile product) : ObservableObject
{
    private AppSettings _settings = new();

    /// <summary>Dil klasöründe bulunan diller; kullanıcı kendi dosyasını ekleyebilir.</summary>
    public IReadOnlyList<LanguagePack> Languages => AppLocalizer.Instance.Available;

    [ObservableProperty]
    private LanguagePack? _selectedLanguage;

    public string LanguageDirectory => AppLocalizer.Instance.LanguageDirectory;

    /// <summary>Araç kartları ürüne göre gösteriliyor; kullanılmayan araç ekranda durmasın.</summary>
    public bool SupportsSteam => product.Supports(PublishProviderId.Steam);

    public bool SupportsEpic => product.Supports(PublishProviderId.Epic);

    [ObservableProperty]
    private string _steamCmdPath = "";

    /// <summary>
    /// Kullanıcının BuildPatchTool kopyasının yolu. SteamCMD'nin aksine indirme
    /// seçeneği <b>yok</b>: araç Epic Dev Portal'ın arkasında ve dağıtma hakkımız yok.
    /// </summary>
    [ObservableProperty]
    private string _buildPatchToolPath = "";

    [ObservableProperty]
    private int _stallWarningSeconds = 120;

    [ObservableProperty]
    private int _sessionCheckTimeoutSeconds = 45;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    public string WorkspaceRoot => layout.Root;

    public string ProfilesFile => layout.ProfilesFile;

    public async Task LoadAsync()
    {
        _settings = await store.LoadAsync();

        SteamCmdPath = _settings.SteamCmdPath ?? "";
        BuildPatchToolPath = _settings.BuildPatchToolPath ?? "";

        OnPropertyChanged(nameof(Languages));
        SelectedLanguage = Languages.FirstOrDefault(
            l => string.Equals(l.Code, AppLocalizer.Instance.CurrentCode, StringComparison.OrdinalIgnoreCase));
        StallWarningSeconds = _settings.StallWarningSeconds;
        SessionCheckTimeoutSeconds = _settings.SessionCheckTimeoutSeconds;
        StatusMessage = "";
    }

    /// <summary>
    /// Dil seçimi anında uygulanır ve kaydedilir — "Kaydet"e basmayı beklemek,
    /// değişikliğin işe yarayıp yaramadığını görmeyi geciktirirdi.
    /// </summary>
    partial void OnSelectedLanguageChanged(LanguagePack? value)
    {
        if (value is null || string.Equals(value.Code, AppLocalizer.Instance.CurrentCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppLocalizer.Instance.Use(value.Code);
        _settings.Language = value.Code;
        _ = store.SaveAsync(_settings);
    }

    [RelayCommand]
    private void OpenLanguageFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppLocalizer.Instance.LanguageDirectory,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _settings.SteamCmdPath = SteamCmdPath is { Length: > 0 } ? SteamCmdPath : null;
        _settings.BuildPatchToolPath = BuildPatchToolPath is { Length: > 0 } ? BuildPatchToolPath : null;
        _settings.StallWarningSeconds = Math.Max(10, StallWarningSeconds);
        _settings.SessionCheckTimeoutSeconds = Math.Max(10, SessionCheckTimeoutSeconds);

        await store.SaveAsync(_settings);
        StatusMessage = AppLocalizer.Instance.Get("Settings.Saved");
    }

    [RelayCommand]
    private void BrowseSteamCmd()
    {
        var initial = SteamCmdPath is { Length: > 0 }
            ? System.IO.Path.GetDirectoryName(SteamCmdPath)
            : null;

        if (dialogs.PickExecutable(AppLocalizer.Instance.Get("Settings.SteamCmd.Pick.Tip"), initial) is { } picked)
        {
            SteamCmdPath = picked;
        }
    }

    /// <summary>
    /// Yolu boşaltır ve uygulamanın kendi kurulumunu indirmesini sağlar
    /// (indirme + bootstrap; ilk çalıştırmada steamcmd kendini günceller).
    /// </summary>
    [RelayCommand]
    private async Task DownloadSteamCmdAsync()
    {
        IsBusy = true;

        try
        {
            _settings.SteamCmdPath = null;
            await store.SaveAsync(_settings);

            var progress = new Progress<string>(text => StatusMessage = text);
            var installation = await coordinator.EnsureInstallationAsync(progress);

            SteamCmdPath = installation.ExecutablePath;
            await LoadAsync();
            StatusMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.Ready");
        }
        catch (Exception ex)
        {
            StatusMessage = AppLocalizer.Instance.Format("Settings.SteamCmd.DownloadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Girilen yolun gerçekten bir SteamCMD kurulumu olduğunu doğrular.</summary>
    [RelayCommand]
    private void VerifySteamCmd()
    {
        if (SteamCmdPath.Length == 0)
        {
            StatusMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.Empty");
            return;
        }

        StatusMessage = SteamCmdProvisioner.LocateExecutable(SteamCmdPath) is { } located
            ? AppLocalizer.Instance.Format("Settings.SteamCmd.Valid", located)
            : AppLocalizer.Instance.Get("Settings.SteamCmd.NotFound");
    }

    /// <summary>
    /// BuildPatchTool'un yerini sorar. Kullanıcı exe'yi de, zip'in açıldığı kök
    /// klasörü de gösterebilir; ikisi de kabul ediliyor.
    /// </summary>
    [RelayCommand]
    private void BrowseBuildPatchTool()
    {
        var initial = BuildPatchToolPath is { Length: > 0 }
            ? System.IO.Path.GetDirectoryName(BuildPatchToolPath)
            : null;

        if (dialogs.PickExecutable(AppLocalizer.Instance.Get("Epic.Tool.Pick.Tip"), initial) is { } picked)
        {
            BuildPatchToolPath = picked;
        }
    }

    /// <summary>Girilen yolda gerçekten bir BuildPatchTool var mı ve hangi sürüm.</summary>
    [RelayCommand]
    private void VerifyBuildPatchTool()
    {
        if (BuildPatchToolPath.Length == 0)
        {
            StatusMessage = AppLocalizer.Instance.Get("Epic.Tool.Empty");
            return;
        }

        if (BptInstallation.LocateExecutable(BuildPatchToolPath) is not { } located)
        {
            StatusMessage = AppLocalizer.Instance.Get("Epic.Tool.NotFound");
            return;
        }

        var version = new BptInstallation(located).VersionFromPath();

        StatusMessage = version is { Length: > 0 }
            ? AppLocalizer.Instance.Format("Epic.Tool.ValidVersion", located, version)
            : AppLocalizer.Instance.Format("Settings.SteamCmd.Valid", located);
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = layout.Root,
            UseShellExecute = true,
        });
    }
}
