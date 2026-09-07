using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Models;

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

    public ShellViewModel(
        ProfileRepository repository,
        NavigationState navigation,
        ProfilePickerViewModel profilePicker,
        AppPickerViewModel appPicker,
        SubAppWorkspaceViewModel workspace,
        SettingsViewModel settings,
        LoginViewModel login,
        SetupViewModel setup)
    {
        _repository = repository;
        Navigation = navigation;
        ProfilePicker = profilePicker;
        AppPicker = appPicker;
        Workspace = workspace;
        Settings = settings;
        Login = login;
        Setup = setup;

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

    public SettingsViewModel Settings { get; }

    public LoginViewModel Login { get; }

    public SetupViewModel Setup { get; }

    [ObservableProperty]
    private ShellPage _currentPage = ShellPage.ProfilePicker;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsProfilePicker => CurrentPage == ShellPage.ProfilePicker;

    public bool IsAppPicker => CurrentPage == ShellPage.AppPicker;

    public bool IsWorkspace => CurrentPage == ShellPage.SubAppWorkspace;

    public bool IsSettings => CurrentPage == ShellPage.Settings;

    public bool IsSetup => CurrentPage == ShellPage.Setup;

    /// <summary>Kurulum sırasında breadcrumb ve ayarlar butonu gizlenir.</summary>
    public bool IsChromeVisible => CurrentPage != ShellPage.Setup;

    /// <summary>Breadcrumb'daki profil rozeti için: seçili profilin avatarı.</summary>
    public string? ActiveAvatarPath =>
        ProfilePicker.Cards.FirstOrDefault(c => c.Profile == Navigation.Profile)?.AvatarPath;

    public bool HasActiveAvatar => ActiveAvatarPath is { Length: > 0 };

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
        else if (CurrentPage == ShellPage.SubAppWorkspace)
        {
            Workspace.Refresh();
        }
    }

    partial void OnCurrentPageChanged(ShellPage value)
    {
        OnPropertyChanged(nameof(IsProfilePicker));
        OnPropertyChanged(nameof(IsAppPicker));
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsChromeVisible));
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
