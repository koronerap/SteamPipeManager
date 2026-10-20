using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Widget uygulaması: tepsi simgesi, her zaman üstteki şerit ve ayrıntı penceresi.
/// Penceresi kapansa da çalışıyor; yalnızca menüdeki "Çık" kapatıyor.
/// </summary>
public sealed class WidgetApp : Application
{
    private WidgetHost? _host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Name = WidgetMode.DisplayName;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var layout = WorkspaceLayout.Default();
            layout.EnsureCreated();

            // Zaten açıksa ona "görün" denip bu kopya kapanıyor.
            if (WidgetInstance.TryAcquire(layout) is not { } instance)
            {
                Environment.Exit(0);
                return;
            }

            App.InitializeLanguage(layout);

            _host = new WidgetHost(this, desktop, layout, instance);
            _ = _host.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Widget'ın parçalarını kurar ve birbirine bağlar.</summary>
internal sealed class WidgetHost
{
    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WidgetInstance _instance;
    private readonly WidgetViewModel _widget;
    private readonly AnalyticsRefresher _refresher;
    private readonly AnalyticsViewModel _analytics;
    private readonly StripWindow _strip;
    private readonly TrayIcon _tray;
    private readonly NativeMenu _trayMenu = new();
    private string? _menuSignature;
    private DetailWindow? _details;

    public WidgetHost(Application app, IClassicDesktopStyleApplicationLifetime desktop, WorkspaceLayout layout, WidgetInstance instance)
    {
        _app = app;
        _desktop = desktop;
        _instance = instance;

        var analytics = AnalyticsServices.Create(layout);
        var profiles = new ProfileRepository(new JsonProfileStore(layout));

        _widget = new WidgetViewModel(analytics.Service, profiles, analytics.Keys, layout);

        // Ayrıntı penceresi bütün oyunları gösteriyor; hepsi, her biri kendi profilinin
        // anahtarıyla yenileniyor.
        _refresher = new AnalyticsRefresher(analytics.Service, () => _widget.Plan.Scope);

        _analytics = new AnalyticsViewModel(
            analytics.Service, _refresher, profiles, new SteamImageService(layout.CoversDirectory), analytics.Keys);

        _strip = new StripWindow { DataContext = _widget };
        _strip.DetailsRequested += (_, _) => _ = ShowDetailsAsync();
        _strip.AnchorChanged += async (_, anchor) =>
        {
            _widget.Settings.StripRight = anchor.X;
            _widget.Settings.StripBottom = anchor.Y;
            await _widget.SaveAsync();
        };

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://{typeof(WidgetApp).Assembly.GetName().Name}/Assets/app.ico"))),
            ToolTipText = WidgetMode.DisplayName,
            Menu = _trayMenu,
        };
        _tray.Clicked += (_, _) => _ = ShowDetailsAsync();
        TrayIcon.SetIcons(app, [_tray]);
    }

    public async Task StartAsync()
    {
        await _widget.LoadAsync();

        _widget.Changed += (_, _) =>
        {
            _strip.ApplyLook(_widget.Settings);
            UpdateMenus();
        };
        _refresher.Updated += async (_, _) => await _widget.ReloadAsync();

        // Ana uygulamadaki "Widget'ı aç" düğmesi: şerit gizliyse geri getir; her zaman üstte
        // değilse başka pencerelerin arkasında kalmış olabilir, öne al.
        _instance.ShowRequested += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SetStripVisible(true);

            if (!_widget.Settings.AlwaysOnTop)
            {
                _strip.Activate();
            }
        });

        // Güncelleme dosyaları değiştirecek; ana uygulama sonra yeniden açıyor.
        _instance.QuitRequested += (_, _) => Dispatcher.UIThread.Post(Quit);

        _strip.ApplyLook(_widget.Settings);
        _strip.SetAnchor(_widget.Settings.StripRight, _widget.Settings.StripBottom);
        UpdateMenus();

        if (_widget.Settings.StripVisible)
        {
            _strip.Show();
        }

        _refresher.Start();
    }

    private void UpdateMenus()
    {
        var entries = MenuEntries();
        var signature = WidgetMenu.Signature(entries);

        // Her yenileme turunda çağrılıyor; içerik değişmediyse menüye dokunulmuyor.
        if (signature != _menuSignature)
        {
            _menuSignature = signature;
            WidgetMenu.Fill(_trayMenu, entries);
            _strip.SetMenu(entries);
        }

        // Windows tepsi ipucu 127 karakterle sınırlı.
        var tooltip = _widget.Tooltip;
        _tray.ToolTipText = tooltip.Length <= 120 ? tooltip : tooltip[..119] + "…";
    }

    private List<WidgetMenuEntry> MenuEntries()
    {
        var t = AppLocalizer.Instance;

        var games = _widget.Targets
            .Select(target => new WidgetMenuEntry(
                target.Title,
                () => _ = _widget.TogglePinnedAsync(target.AppId),
                IsChecked: _widget.IsPinned(target.AppId)))
            .ToList();

        var metrics = new[]
        {
            (WidgetMetric.Titles, "Widget.Metric.Titles"),
            (WidgetMetric.Players, "Widget.Metric.Players"),
            (WidgetMetric.Wishlists, "Widget.Metric.Wishlists"),
            (WidgetMetric.Sales, "Widget.Metric.Sales"),
        }
        .Select(m => new WidgetMenuEntry(t.Get(m.Item2), () => _ = _widget.ToggleMetricAsync(m.Item1), IsChecked: _widget.IsShown(m.Item1)))
        .ToList();

        var settings = _widget.Settings;

        // Seçeneklerden biri seçili bir alt menü: "Metin boyutu ▸ Küçük / Normal / ...".
        WidgetMenuEntry Choice<T>(string key, T current, Action<WidgetSettings, T> set) where T : struct, Enum =>
            new(t.Get(key), Children: Enum.GetValues<T>()
                .Select(value => new WidgetMenuEntry(
                    t.Get($"{key}.{value}"),
                    () => _ = _widget.UpdateAsync(s => set(s, value)),
                    IsChecked: EqualityComparer<T>.Default.Equals(value, current)))
                .ToList());

        List<WidgetMenuEntry> appearance =
        [
            Choice("Widget.TextSize", settings.TextSize, (s, v) => s.TextSize = v),
            Choice("Widget.Orientation", settings.Orientation, (s, v) => s.Orientation = v),
            Choice("Widget.Transparency", settings.Transparency, (s, v) => s.Transparency = v),
            Choice("Widget.Theme", settings.Theme, (s, v) => s.Theme = v),
            WidgetMenuEntry.Separator,
            new(t.Get("Widget.AlwaysOnTop"), () => _ = _widget.UpdateAsync(s => s.AlwaysOnTop = !s.AlwaysOnTop), IsChecked: settings.AlwaysOnTop),
            new(t.Get("Widget.LockPosition"), () => _ = _widget.UpdateAsync(s => s.LockPosition = !s.LockPosition), IsChecked: settings.LockPosition),
            new(t.Get("Widget.ResetPosition"), () => _ = ResetPositionAsync()),
        ];

        return
        [
            new(t.Get("Widget.OpenDetails"), () => _ = ShowDetailsAsync()),
            new(t.Get("Widget.ShowStrip"), () => SetStripVisible(!_widget.Settings.StripVisible), IsChecked: _widget.Settings.StripVisible),
            WidgetMenuEntry.Separator,
            new(t.Get("Widget.Games"), Children: games.Count > 0 ? games : [new(t.Get("Widget.NoGames"))]),
            new(t.Get("Widget.Show"), Children: metrics),
            new(t.Get("Widget.Appearance"), Children: appearance),
            WidgetMenuEntry.Separator,
            new(t.Get("Analytics.Refresh"), () => _refresher.RefreshSoon(force: true)),
            new(t.Format("Widget.OpenApp", App.CurrentProduct.Name), () => MainAppLauncher.Launch()),
            new(t.Get("Widget.StartWithSystem"), () => { WidgetAutoStart.Set(!WidgetAutoStart.IsEnabled); UpdateMenus(); }, IsChecked: WidgetAutoStart.IsEnabled),
            WidgetMenuEntry.Separator,
            new(t.Get("Widget.Quit"), Quit),
        ];
    }

    private void SetStripVisible(bool visible)
    {
        _widget.Settings.StripVisible = visible;
        _ = _widget.SaveAsync();

        if (visible)
        {
            _strip.Show();
        }
        else
        {
            _strip.Hide();
        }

        UpdateMenus();
    }

    private async Task ResetPositionAsync()
    {
        await _widget.ResetPositionAsync();
        _strip.SetAnchor(null, null);
    }

    private async Task ShowDetailsAsync()
    {
        _details ??= new DetailWindow(_analytics, () => MainAppLauncher.Launch());
        await _details.ShowAndLoadAsync();
    }

    private void Quit()
    {
        _refresher.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        _instance.Dispose();
        _desktop.Shutdown();
    }
}
