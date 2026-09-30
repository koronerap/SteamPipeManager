using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.App.ViewModels;

public enum ShellPage
{
    ProfilePicker,
    AppPicker,
    SubAppWorkspace,
    Settings,
    Setup,
}

/// <summary>
/// Pencerenin kök ViewModel'i. Hangi sayfanın gösterileceğini gezinme durumundan türetir:
/// profil yoksa profil seçimi, oyun yoksa oyun seçimi, ikisi de varsa yönetim ekranı.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ProfileRepository _repository;

    private readonly IConfirmationService _confirmation;

    /// <summary>
    /// Kapanışta kaydın bekleneceği en uzun süre. Disk takılırsa pencere sonsuza kadar
    /// açık kalmasın; süre dolarsa kullanıcıya sorulur.
    /// </summary>
    public static readonly TimeSpan CloseSaveTimeout = TimeSpan.FromSeconds(10);

    public ShellViewModel(
        ProfileRepository repository,
        NavigationState navigation,
        ProfilePickerViewModel profilePicker,
        AppPickerViewModel appPicker,
        SubAppWorkspaceViewModel workspace,
        EpicWorkspaceViewModel epicWorkspace,
        SettingsViewModel settings,
        LoginViewModel login,
        SetupViewModel setup,
        UpdateViewModel updates,
        IConfirmationService confirmation,
        ProductProfile product)
    {
        _repository = repository;
        _confirmation = confirmation;
        Navigation = navigation;
        ProfilePicker = profilePicker;
        AppPicker = appPicker;
        Workspace = workspace;
        EpicWorkspace = epicWorkspace;
        Settings = settings;
        Login = login;
        Setup = setup;
        Updates = updates;
        Product = product;

        // Sihirbaz bitince normal gezinmeye dönülür.
        setup.Completed += (_, _) => OnNavigationChanged();

        // Yalnızca profil/oyun değişimi sayfa geçişi demek. SubApp'i dinlemek döngü kurardı:
        // Refresh() → Selected → Navigation.SubApp → buraya geri dönüş → Refresh().
        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NavigationState.Profile) or nameof(NavigationState.App))
            {
                OnNavigationChanged();
                NotifyBreadcrumb();
            }
        };

        // Avatar arka planda geldiğinde breadcrumb rozeti de tazelenmeli.
        foreach (var card in profilePicker.Cards)
        {
            card.PropertyChanged += (_, _) => NotifyBreadcrumb();
        }

        profilePicker.Cards.CollectionChanged += (_, args) =>
        {
            foreach (var added in args.NewItems?.OfType<ProfileCard>() ?? [])
            {
                added.PropertyChanged += (_, _) => NotifyBreadcrumb();
            }

            NotifyBreadcrumb();
        };
    }

    public NavigationState Navigation { get; }

    public ProfilePickerViewModel ProfilePicker { get; }

    public AppPickerViewModel AppPicker { get; }

    public SubAppWorkspaceViewModel Workspace { get; }

    public EpicWorkspaceViewModel EpicWorkspace { get; }

    public SettingsViewModel Settings { get; }

    public LoginViewModel Login { get; }

    public SetupViewModel Setup { get; }

    /// <summary>Pencerenin üstündeki güncelleme şeridi ve ayarlardaki kart.</summary>
    public UpdateViewModel Updates { get; }

    /// <summary>
    /// Çalışan ürün. Pencere başlığı buradan geliyor — ürün adları çevrilmiyor,
    /// "Epic Build Manager" her dilde aynı.
    /// </summary>
    public ProductProfile Product { get; }

    public string ProductName => Product.Name;

    [ObservableProperty]
    private ShellPage _currentPage = ShellPage.ProfilePicker;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsProfilePicker => CurrentPage == ShellPage.ProfilePicker;

    public bool IsAppPicker => CurrentPage == ShellPage.AppPicker;

    public bool IsWorkspace => CurrentPage == ShellPage.SubAppWorkspace;

    /// <summary>
    /// Çalışma alanı sağlayıcıya göre değişiyor: Steam'de depot'lu ekran, Epic'te
    /// artifact ekranı. İçerik modelleri farklı olduğu için tek ekranı koşullu
    /// sekmelerle esnetmek yerine iki ayrı ekran tutuluyor.
    /// </summary>
    public bool IsSteamWorkspace =>
        IsWorkspace && Navigation.Profile?.Provider != PublishProviderId.Epic;

    public bool IsEpicWorkspace =>
        IsWorkspace && Navigation.Profile?.Provider == PublishProviderId.Epic;

    public bool IsSettings => CurrentPage == ShellPage.Settings;

    public bool IsSetup => CurrentPage == ShellPage.Setup;

    /// <summary>Kurulum sırasında breadcrumb ve ayarlar butonu gizlenir.</summary>
    public bool IsChromeVisible => CurrentPage != ShellPage.Setup;

    private ProfileCard? ActiveCard =>
        ProfilePicker.Cards.FirstOrDefault(c => c.Profile == Navigation.Profile);

    /// <summary>Breadcrumb'daki profil rozeti için: seçili profilin avatarı.</summary>
    public string? ActiveAvatarPath => ActiveCard?.AvatarPath;

    public bool HasActiveAvatar => ActiveAvatarPath is { Length: > 0 };

    /// <summary>
    /// Rozetin sağ alt köşesindeki oturum noktası. Oturum kontrolü arka planda sürerken
    /// kullanıcı içeri girip ayar yapabildiği için durum profil ekranından çıkınca da
    /// takip edilebilmeli.
    /// </summary>
    public Brush ActiveSessionBrush => ActiveCard?.SessionBrush ?? Brushes.Gray;

    /// <summary>Profil rozetinin tooltip'i: hesap adı ve oturum durumu birlikte.</summary>
    public string ActiveProfileTooltip => (Navigation.Profile?.DisplayName, ActiveCard) switch
    {
        (null, _) => "",
        ({ } name, null) => name,
        ({ } name, { } card) => $"{name} — {card.SessionText}",
    };

    /// <summary>Avatar yoksa profilin baş harfi gösterilir.</summary>
    public string ActiveInitial => Navigation.Profile?.DisplayName is { Length: > 0 } name
        ? name[..1].ToUpperInvariant()
        : "?";

    /// <summary>İlk çalıştırmada sihirbaz gösterilir; sonra normal akışa geçilir.</summary>
    public async Task InitializeAsync(bool setupCompleted)
    {
        await _repository.LoadAsync();
        ProfilePicker.Refresh();

        if (!setupCompleted)
        {
            await Setup.LoadAsync();
            CurrentPage = ShellPage.Setup;
            return;
        }

        // İkisi de ağ/süreç işi olduğu için beklenmez; kartlar sonuç geldikçe güncellenir.
        _ = ProfilePicker.RefreshAvatarsAsync();
        _ = ProfilePicker.AutoCheckSessionsAsync();
    }

    private void NotifyBreadcrumb()
    {
        OnPropertyChanged(nameof(ActiveAvatarPath));
        OnPropertyChanged(nameof(HasActiveAvatar));
        OnPropertyChanged(nameof(ActiveInitial));
        OnPropertyChanged(nameof(ActiveSessionBrush));
        OnPropertyChanged(nameof(ActiveProfileTooltip));
    }

    private void OnNavigationChanged()
    {
        CurrentPage = (Navigation.Profile, Navigation.App) switch
        {
            (null, _) => ShellPage.ProfilePicker,
            (_, null) => ShellPage.AppPicker,
            _ => ShellPage.SubAppWorkspace,
        };

        if (CurrentPage == ShellPage.AppPicker)
        {
            AppPicker.Refresh();
        }
        else if (CurrentPage == ShellPage.SubAppWorkspace &&
                 Navigation.Profile?.Provider == PublishProviderId.Epic)
        {
            EpicWorkspace.Refresh();
        }
        else if (CurrentPage == ShellPage.SubAppWorkspace)
        {
            Workspace.Refresh();
        }
    }

    partial void OnCurrentPageChanged(ShellPage oldValue, ShellPage newValue)
    {
        // Build hedefi ekranı otomatik kaydediyor; sayfadan çıkarken bekleyen yazma
        // gecikmesi beklenmeden diske geçirilir.
        if (oldValue == ShellPage.SubAppWorkspace)
        {
            _ = Workspace.FlushAsync();
            _ = EpicWorkspace.FlushAsync();
        }

        OnPropertyChanged(nameof(IsProfilePicker));
        OnPropertyChanged(nameof(IsAppPicker));
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsSteamWorkspace));
        OnPropertyChanged(nameof(IsEpicWorkspace));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsChromeVisible));
    }

    public bool HasUnsavedChanges => Workspace.HasUnsavedChanges || EpicWorkspace.HasUnsavedChanges;

    /// <summary>
    /// Pencere kapanmadan önce bekleyen düzenlemeleri yazar. Yazılamazsa kullanıcıya
    /// sorar; true dönerse pencere kapanabilir.
    /// </summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        var flush = Task.WhenAll(Workspace.FlushAsync(), EpicWorkspace.FlushAsync());
        var finished = await Task.WhenAny(flush, Task.Delay(CloseSaveTimeout)) == flush;

        if (finished && !HasUnsavedChanges)
        {
            return true;
        }

        var reason = finished
            ? Workspace.LastSaveError ?? EpicWorkspace.LastSaveError ?? ""
            : Localization.AppLocalizer.Instance.Get("Close.SaveTimedOut");

        return await _confirmation.ConfirmAsync(
            Localization.AppLocalizer.Instance.Get("Close.Unsaved.Title"),
            Localization.AppLocalizer.Instance.Format("Close.Unsaved.Body", reason),
            confirmText: Localization.AppLocalizer.Instance.Get("Close.Unsaved.Confirm"),
            isDestructive: true);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await Settings.LoadAsync();
        CurrentPage = ShellPage.Settings;
    }

    /// <summary>Ayarlardan çıkınca gezinme durumuna uygun sayfaya dönülür.</summary>
    [RelayCommand]
    private void CloseSettings() => OnNavigationChanged();

    [RelayCommand]
    private void GoToProfiles()
    {
        Navigation.ClearProfile();
        ProfilePicker.Refresh();

        // Sayfa açıkça ayarlanıyor: gezinme durumu zaten boşsa (ör. Ayarlar profil
        // ekranından açıldıysa) ClearProfile bir değişiklik olayı üretmez ve
        // sayfa Ayarlar'da takılı kalırdı.
        CurrentPage = ShellPage.ProfilePicker;
    }

    [RelayCommand]
    private void GoToApps()
    {
        Navigation.ClearApp();
        CurrentPage = Navigation.HasProfile ? ShellPage.AppPicker : ShellPage.ProfilePicker;
    }

    [RelayCommand]
    private void SelectApp(SteamApp app) => Navigation.SelectApp(app);
}
