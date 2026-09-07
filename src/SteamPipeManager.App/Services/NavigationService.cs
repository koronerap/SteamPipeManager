using CommunityToolkit.Mvvm.ComponentModel;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.App.Services;

/// <summary>
/// Uygulamanın gezinme durumu: profil → oyun → build hedefi.
/// Breadcrumb'ın her parçası bu üç alandan okunur.
/// </summary>
public sealed partial class NavigationState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    private UserProfile? _profile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApp))]
    private SteamApp? _app;

    [ObservableProperty]
    private SubApp? _subApp;

    public bool HasProfile => Profile is not null;

    public bool HasApp => App is not null;

    public void SelectProfile(UserProfile profile)
    {
        Profile = profile;
        App = null;
        SubApp = null;
    }

    public void SelectApp(SteamApp app)
    {
        App = app;
        SubApp = app.SubApps.FirstOrDefault();
    }

    public void ClearProfile()
    {
        Profile = null;
        App = null;
        SubApp = null;
    }

    public void ClearApp()
    {
        App = null;
        SubApp = null;
    }
}
