using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Widget'ın ayrıntı penceresi: analitik sayfasının kendisi. Kapatılınca gizleniyor,
/// widget çalışmaya devam ediyor.
/// </summary>
public sealed partial class DetailWindow : Window
{
    private readonly AnalyticsViewModel _analytics;

    internal DetailWindow(AnalyticsViewModel analytics, Action openMainApp)
    {
        AvaloniaXamlLoader.Load(this);
        _analytics = analytics;

        this.FindControl<Views.AnalyticsView>("Analytics")!.DataContext = analytics;

        // Analitik sayfasındaki "Anahtar ekle" düğmesi pencerenin bağlamındaki ayarlar
        // komutunu arıyor; widget'ta ayarlar ana uygulamada, onu açıyor.
        DataContext = new { OpenSettingsCommand = new RelayCommand(openMainApp) };

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public async Task ShowAndLoadAsync()
    {
        _analytics.IsActive = true;
        Show();
        Activate();
        await _analytics.LoadAsync();
    }

    public new void Hide()
    {
        _analytics.IsActive = false;
        base.Hide();
    }
}
