using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Localization;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.App.ViewModels;

/// <summary>
/// İlk çalıştırma sihirbazı: dili doğrular ve SteamCMD'yi hazırlar.
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
    [NotifyPropertyChangedFor(nameof(NeedsSteamCmd))]
    private bool _steamCmdReady;

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

        // Zaten kurulu bir SteamCMD varsa adım atlanabilir.
        SteamCmdReady = await coordinator.IsInstalledAsync();

        if (SteamCmdReady)
        {
            SteamCmdPath = _settings.SteamCmdPath ?? "";
            StatusMessage = AppLocalizer.Instance.Get("Settings.SteamCmd.Ready");
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
        if (dialogs.PickExecutable(AppLocalizer.Instance.Get("Settings.SteamCmd.Pick.Tip")) is not { } picked)
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

    [RelayCommand]
    private async Task FinishAsync()
    {
        _settings.SetupCompleted = true;
        await store.SaveAsync(_settings);

        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// SteamCMD'yi sonra kurmak isteyen kullanıcı için: ilk build'de indirilecek.
    /// </summary>
    [RelayCommand]
    private Task SkipAsync() => FinishAsync();
}
