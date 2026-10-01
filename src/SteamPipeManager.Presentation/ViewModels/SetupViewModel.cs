using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// İlk çalıştırma sihirbazı: dili doğrular ve ürünün kullandığı yayın aracını hazırlar —
/// Steam için SteamCMD, Epic için BuildPatchTool. Ürünün desteklemediği aracın adımı
/// hiç gösterilmiyor.
///
/// SteamCMD kurulumu ilk build'e bırakılabilirdi ama o zaman kullanıcı ilk kez
/// build alırken 43 MB'lık bir indirmeyle karşılaşırdı. Burada bir kez hallediliyor.
/// </summary>
public sealed partial class SetupViewModel(
    ISettingsStore store,
    BuildCoordinator coordinator,
    IDialogService dialogs,
    ProductProfile product) : ObservableObject
{
    /// <summary>
    /// Karşılama başlığı ürün adını taşıyor — üç üründen hangisi çalışıyorsa onu.
    /// Ürün adları çevrilmiyor, cümle çevriliyor.
    /// </summary>
    public string WelcomeText => AppLocalizer.Instance.Format("Setup.Welcome", product.Name);

    private AppSettings _settings = new();

    public IReadOnlyList<LanguagePack> Languages => AppLocalizer.Instance.Available;

    [ObservableProperty]
    private LanguagePack? _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSteamCmd), nameof(ShowLater))]
    private bool _steamCmdReady;

    public bool SupportsSteam => product.Supports(PublishProviderId.Steam);

    public bool SupportsEpic => product.Supports(PublishProviderId.Epic);

    /// <summary>
    /// BuildPatchTool'un yeri. İndirme seçeneği yok: araç Epic Dev Portal'ın arkasında
    /// ve dağıtma hakkımız yok, kullanıcının göstermesi gerekiyor.
    /// </summary>
    [ObservableProperty]
    private string _buildPatchToolPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsBuildPatchTool), nameof(ShowLater))]
    private bool _buildPatchToolReady;

    [ObservableProperty]
    private string _buildPatchToolStatus = "";

    [ObservableProperty]
    private string? _buildPatchToolError;

    public bool NeedsBuildPatchTool => !BuildPatchToolReady;

    /// <summary>"Sonra yaparım" yalnızca eksik bir araç varsa anlamlı.</summary>
    public bool ShowLater =>
        (SupportsSteam && NeedsSteamCmd) || (SupportsEpic && NeedsBuildPatchTool);

    [ObservableProperty]
    private string _steamCmdPath = "";

    public bool IsIdle => !IsBusy;

    public bool NeedsSteamCmd => !SteamCmdReady;

    /// <summary>Sihirbaz tamamlandığında tetiklenir.</summary>
    public event EventHandler? Completed;

    public async Task LoadAsync()
    {
        _settings = await store.LoadAsync();

        OnPropertyChanged(nameof(Languages));
        SelectedLanguage = Languages.FirstOrDefault(
            l => string.Equals(l.Code, AppLocalizer.Instance.CurrentCode, StringComparison.OrdinalIgnoreCase));

        if (SupportsSteam)
        {
            // Zaten kurulu bir SteamCMD varsa adım atlanabilir.
            SteamCmdReady = await coordinator.IsInstalledAsync();

            if (SteamCmdReady)
            {
                SteamCmdPath = _settings.SteamCmdPath ?? "";
                StatusMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.Ready");
            }
        }

        if (SupportsEpic && _settings.BuildPatchToolPath is { Length: > 0 } saved &&
            BptInstallation.LocateExecutable(saved) is { } located)
        {
            ShowBuildPatchTool(located);
        }
    }

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
    private async Task DownloadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var progress = new Progress<string>(text => StatusMessage = text);
            var installation = await coordinator.EnsureInstallationAsync(progress);

            SteamCmdPath = installation.ExecutablePath;
            SteamCmdReady = true;
            StatusMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.Ready");
        }
        catch (Exception ex)
        {
            ErrorMessage = AppLocalizer.Instance.Format("Settings.SteamCmd.DownloadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Elinde zaten bir SteamCMD/ContentBuilder varsa onu gösterebilir.</summary>
    [RelayCommand]
    private async Task PickExistingAsync()
    {
        if (await dialogs.PickExecutableAsync(AppLocalizer.Instance.Get("Settings.SteamCmd.Pick.Tip")) is not { } picked)
        {
            return;
        }

        if (SteamCmdProvisioner.LocateExecutable(picked) is not { } located)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.NotFound");
            return;
        }

        SteamCmdPath = located;
        SteamCmdReady = true;
        ErrorMessage = null;
        StatusMessage = AppLocalizer.Instance.Format("Settings.SteamCmd.Valid", located);

        _settings.SteamCmdPath = located;
        await store.SaveAsync(_settings);
    }

    /// <summary>Kullanıcı exe'yi de, zip'in açıldığı klasörü de gösterebilir.</summary>
    [RelayCommand]
    private async Task PickBuildPatchToolAsync()
    {
        if (await dialogs.PickExecutableAsync(AppLocalizer.Instance.Get("Epic.Tool.Pick.Tip")) is not { } picked)
        {
            return;
        }

        if (BptInstallation.LocateExecutable(picked) is not { } located)
        {
            BuildPatchToolError = AppLocalizer.Instance.Get("Epic.Tool.NotFound");
            return;
        }

        ShowBuildPatchTool(located);

        _settings.BuildPatchToolPath = located;
        await store.SaveAsync(_settings);
    }

    private void ShowBuildPatchTool(string located)
    {
        BuildPatchToolPath = located;
        BuildPatchToolReady = true;
        BuildPatchToolError = null;

        var version = new BptInstallation(located).VersionFromPath();

        BuildPatchToolStatus = version is { Length: > 0 }
            ? AppLocalizer.Instance.Format("Epic.Tool.ValidVersion", located, version)
            : AppLocalizer.Instance.Format("Settings.SteamCmd.Valid", located);
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        _settings.SetupCompleted = true;
        await store.SaveAsync(_settings);

        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Aracı sonra kurmak isteyen kullanıcı için: SteamCMD ilk build'de indiriliyor,
    /// BuildPatchTool'u Ayarlar'dan gösterebiliyor.
    /// </summary>
    [RelayCommand]
    private Task SkipAsync() => FinishAsync();
}
