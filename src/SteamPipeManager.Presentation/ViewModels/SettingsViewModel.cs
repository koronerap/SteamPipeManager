using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Makine düzeyindeki ayarlar: SteamCMD kurulumu, tema ve zaman aşımı süreleri.
/// </summary>
public sealed partial class SettingsViewModel(
    ISettingsStore store,
    WorkspaceLayout layout,
    BuildCoordinator coordinator,
    IDialogService dialogs,
    UpdateViewModel updates,
    ProductProfile product,
    AnalyticsServices analytics,
    AnalyticsRefresher analyticsRefresher,
    ProfileRepository profiles) : ObservableObject
{
    /// <summary>Steamworks'te Financial API Group'un oluşturulduğu sayfa.</summary>
    public const string FinancialGroupsUrl = "https://partner.steamgames.com/pub/groups/";

    private AppSettings _settings = new();

    private bool _loading;

    public UpdateViewModel Updates => updates;

    /// <summary>Açılışta yeni sürüm sorulsun mu; değişiklik anında kaydediliyor.</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.CheckForUpdates = value;
        _ = store.SaveAsync(_settings);
    }

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

    /// <summary>
    /// Steam profilleri ve finans anahtarları. Anahtar bir Steamworks ortağının (şirketin)
    /// verisini görüyor; farklı şirketlerin profilleri kendi anahtarını istiyor.
    /// </summary>
    public ObservableCollection<FinancialKeyRow> FinancialKeys { get; } = [];

    public bool HasFinancialProfiles => FinancialKeys.Count > 0;

    /// <summary>İlk sürümde profile bağlı olmadan girilmiş anahtar; bir profile taşınabiliyor.</summary>
    [ObservableProperty]
    private bool _hasLegacyFinancialKey;

    /// <summary>Anahtarın nerede durduğu; dosyada şifresiz duruyorsa açıkça söyleniyor.</summary>
    public string FinancialStorageText => AppLocalizer.Instance.Get(analytics.Keys.Kind switch
    {
        SecretStorageKind.MacKeychain => "Settings.Financial.Storage.Keychain",
        SecretStorageKind.LinuxSecretService => "Settings.Financial.Storage.SecretService",
        SecretStorageKind.OwnerOnlyFile => "Settings.Financial.Storage.File",
        _ => "Settings.Financial.Storage.Dpapi",
    });

    public bool IsFinancialKeyUnencrypted => !analytics.Keys.IsEncrypted;

    /// <summary>
    /// Satış tutarlarının yanında gösterilen para birimi. İlki otomatik: kullanıcının
    /// bulunduğu ülkeninki (Windows'ta "Ülke veya bölge" ayarından).
    /// </summary>
    public IReadOnlyList<CurrencyOption> CurrencyOptions { get; } =
    [
        new(null, AppLocalizer.Instance.Format("Settings.Currency.Auto", Currencies.OfHomeRegion() ?? "USD")),
        new("USD", AppLocalizer.Instance.Get("Settings.Currency.UsdOnly")),
        .. Currencies.Offered.Where(c => c != "USD").Select(c => new CurrencyOption(c, $"{c} ({Currencies.SymbolOf(c).Trim()})")),
    ];

    [ObservableProperty]
    private CurrencyOption? _selectedCurrency;

    partial void OnSelectedCurrencyChanged(CurrencyOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        analytics.Preferences.Current.Currency = value.Code;
        _ = analytics.Preferences.SaveAsync();
    }

    public string ProfilesFile => layout.ProfilesFile;

    public async Task LoadAsync()
    {
        _settings = await store.LoadAsync();

        var preferences = await analytics.Preferences.LoadAsync();

        _loading = true;
        CheckForUpdates = _settings.CheckForUpdates;
        SelectedCurrency = CurrencyOptions.FirstOrDefault(o => o.Code == preferences.Currency) ?? CurrencyOptions[0];
        _loading = false;

        SteamCmdPath = _settings.SteamCmdPath ?? "";
        BuildPatchToolPath = _settings.BuildPatchToolPath ?? "";

        OnPropertyChanged(nameof(Languages));
        SelectedLanguage = Languages.FirstOrDefault(
            l => string.Equals(l.Code, AppLocalizer.Instance.CurrentCode, StringComparison.OrdinalIgnoreCase));
        StallWarningSeconds = _settings.StallWarningSeconds;
        SessionCheckTimeoutSeconds = _settings.SessionCheckTimeoutSeconds;
        StatusMessage = "";

        LoadFinancialKeys();
    }

    private void LoadFinancialKeys()
    {
        FinancialKeys.Clear();

        if (SupportsSteam)
        {
            foreach (var profile in profiles.Profiles.Where(p => p.Provider == PublishProviderId.Steam))
            {
                FinancialKeys.Add(new FinancialKeyRow(profile.Id, profile.DisplayName, analytics.Keys.Has(profile.Id), SaveFinancialKeyAsync, RemoveFinancialKey, MoveLegacyFinancialKey));
            }
        }

        OnPropertyChanged(nameof(HasFinancialProfiles));
        HasLegacyFinancialKey = SupportsSteam && analytics.Keys.HasLegacy;
    }

    /// <summary>
    /// Anahtarı Steam'e karşı dener, çalışıyorsa profile kaydeder. Steam reddederse
    /// kaydedilmiyor; Steam'e ulaşılamazsa kaydediliyor ama denenemediği söyleniyor.
    /// </summary>
    private async Task SaveFinancialKeyAsync(FinancialKeyRow row)
    {
        var key = row.Input.Trim();

        if (key.Length == 0)
        {
            row.Message = AppLocalizer.Instance.Get("Settings.Financial.Empty");
            return;
        }

        row.IsTesting = true;
        row.Message = AppLocalizer.Instance.Get("Settings.Financial.Testing");

        try
        {
            await analytics.KeyTester.GetChangedDatesAsync(key, highwatermark: ulong.MaxValue);
            row.Message = AppLocalizer.Instance.Get("Settings.Financial.Saved");
        }
        catch (AnalyticsException ex) when (ex.Failure == AnalyticsFailure.InvalidKey)
        {
            row.Message = AppLocalizer.Instance.Get("Settings.Financial.Rejected");
            return;
        }
        catch (AnalyticsException)
        {
            row.Message = AppLocalizer.Instance.Get("Settings.Financial.Untested");
        }
        finally
        {
            row.IsTesting = false;
        }

        try
        {
            analytics.Keys.Write(row.ProfileId, key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            row.Message = AppLocalizer.Instance.Format("Settings.Financial.SaveFailed", ex.Message);
            return;
        }

        row.Input = "";
        row.IsEditing = false;
        row.HasKey = true;

        // Wishlist ve satış geçmişi hemen inmeye başlasın.
        analyticsRefresher.RefreshSoon(force: true);
    }

    private void RemoveFinancialKey(FinancialKeyRow row)
    {
        analytics.Keys.Remove(row.ProfileId);
        row.HasKey = false;
        row.IsEditing = false;
        row.Message = AppLocalizer.Instance.Get("Settings.Financial.Removed");
    }

    /// <summary>İlk sürümden kalan anahtarı, hangi şirkete aitse o profile taşır.</summary>
    private void MoveLegacyFinancialKey(FinancialKeyRow row)
    {
        analytics.Keys.MoveLegacyTo(row.ProfileId);
        HasLegacyFinancialKey = false;
        row.HasKey = analytics.Keys.Has(row.ProfileId);
        row.Message = AppLocalizer.Instance.Get("Settings.Financial.Moved");
        analyticsRefresher.RefreshSoon(force: true);
    }

    [RelayCommand]
    private void DeleteLegacyFinancialKey()
    {
        analytics.Keys.DeleteLegacy();
        HasLegacyFinancialKey = false;
    }

    [RelayCommand]
    private void OpenFinancialGroupsPage() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = FinancialGroupsUrl,
            UseShellExecute = true,
        });

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
    private async Task BrowseSteamCmdAsync()
    {
        var initial = SteamCmdPath is { Length: > 0 }
            ? System.IO.Path.GetDirectoryName(SteamCmdPath)
            : null;

        if (await dialogs.PickExecutableAsync(AppLocalizer.Instance.Get("Settings.SteamCmd.Pick.Tip"), initial) is { } picked)
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
    private async Task BrowseBuildPatchToolAsync()
    {
        var initial = BuildPatchToolPath is { Length: > 0 }
            ? System.IO.Path.GetDirectoryName(BuildPatchToolPath)
            : null;

        if (await dialogs.PickExecutableAsync(AppLocalizer.Instance.Get("Epic.Tool.Pick.Tip"), initial) is { } picked)
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

/// <summary>Ayarlarda bir Steam profilinin finans anahtarı satırı.</summary>
public sealed partial class FinancialKeyRow(
    Guid profileId,
    string profileName,
    bool hasKey,
    Func<FinancialKeyRow, Task> save,
    Action<FinancialKeyRow> remove,
    Action<FinancialKeyRow> moveLegacy) : ObservableObject
{
    public Guid ProfileId { get; } = profileId;

    public string ProfileName { get; } = profileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _hasKey = hasKey;

    /// <summary>Anahtar giriliyor mu; kayıtlı anahtar geri okunup gösterilmiyor.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Yapıştırılan anahtar; yalnızca kaydedilene kadar bellekte.</summary>
    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isTesting;

    public string StatusText => AppLocalizer.Instance.Get(HasKey ? "Settings.Financial.Present" : "Settings.Financial.Missing");

    public string EditText => AppLocalizer.Instance.Get(HasKey ? "Settings.Financial.Change" : "Settings.Financial.Add");

    partial void OnHasKeyChanged(bool value) => OnPropertyChanged(nameof(EditText));

    [RelayCommand]
    private void Edit()
    {
        Input = "";
        Message = "";
        IsEditing = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        Input = "";
        IsEditing = false;
    }

    [RelayCommand]
    private Task SaveAsync() => save(this);

    [RelayCommand]
    private void Remove() => remove(this);

    [RelayCommand]
    private void MoveLegacy() => moveLegacy(this);

    public override string ToString() => ProfileName;
}

/// <summary>Ayarlardaki para birimi seçeneği; <see cref="Code"/> null ise otomatik.</summary>
public sealed record CurrencyOption(string? Code, string Label)
{
    public override string ToString() => Label;
}
