using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Desktop.Services;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop;

/// <summary>
/// Açılış akışı WPF uygulamasınınkiyle aynı: dil, hizmetler, profiller, pencere,
/// ardından güncelleme kontrolü. Mantığın tamamı paylaşılan katmanda.
/// </summary>
public sealed class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Pencere hemen gösteriliyor; yükleme onun ardından. Avalonia'da açılış
            // bekletilemiyor ve boş bir pencere, hiç pencere olmamasından iyi.
            var layout = WorkspaceLayout.Default();
            layout.EnsureCreated();

            InitializeLanguage(layout);

            _services = BuildServices(layout, desktop);

            var window = _services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _services?.Dispose();

            _ = StartAsync(window, layout, desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(MainWindow window, WorkspaceLayout layout, IReadOnlyList<string> args)
    {
        var shell = _services!.GetRequiredService<ShellViewModel>();

        try
        {
            await shell.InitializeAsync(await IsSetupCompletedAsync(layout));
        }
        catch (Exception ex)
        {
            shell.StatusMessage = ex.Message;
        }

        window.DataContext = shell;

        // Pencere açıldıktan sonra: ağ beklemesi açılışı geciktirmesin.
        _ = shell.Updates.StartAsync(args);
    }

    /// <summary>
    /// Dil dosyalarını hazırlar. İlk çalıştırmada (ayarda dil yoksa) sistem diline göre
    /// seçilir; eşleşme yoksa İngilizce kullanılır ve seçim ayarlara yazılır.
    /// </summary>
    private static void InitializeLanguage(WorkspaceLayout layout)
    {
        AppLocalizer.Instance.Initialize(EmbeddedLanguages.Read(), layout.LanguageDirectory);

        var store = new JsonSettingsStore(layout);

        try
        {
            var settings = store.LoadAsync().GetAwaiter().GetResult();

            if (settings.Language is { Length: > 0 } saved)
            {
                AppLocalizer.Instance.Use(saved);
                return;
            }

            var detected = AppLocalizer.Instance.ResolveSystemLanguage();
            AppLocalizer.Instance.Use(detected);

            settings.Language = detected;
            store.SaveAsync(settings).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            AppLocalizer.Instance.Use(Localizer.FallbackCode);
        }
    }

    private static async Task<bool> IsSetupCompletedAsync(WorkspaceLayout layout)
    {
        try
        {
            return (await new JsonSettingsStore(layout).LoadAsync()).SetupCompleted;
        }
        catch (Exception)
        {
            // Ayarlar okunamazsa sihirbaz gösterilir; zararsız taraf bu.
            return false;
        }
    }

    private static ServiceProvider BuildServices(WorkspaceLayout layout, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var services = new ServiceCollection();

        services.AddSingleton(layout);
        services.AddSingleton(AppLocalizer.Instance);
        services.AddSingleton<IProfileStore>(_ => new JsonProfileStore(layout));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(layout));
        services.AddSingleton<ProfileRepository>();
        services.AddSingleton<NavigationState>();
        services.AddSingleton<SteamCmdProvisioner>();
        services.AddSingleton(_ => new SteamImageService(layout.CoversDirectory));

        // Hangi ürün çalışıyor: derleme sırasında assembly meta verisine yazılıyor.
        services.AddSingleton(_ => ProductProfile.Parse(
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SpmProduct")?.Value));

        // Epic client secret'ları: macOS'ta Anahtar Zinciri, Linux'ta gizli bilgi servisi.
        services.AddSingleton(_ => new EpicSecretStore(layout.EpicSecretsFile));

        services.AddSingleton<MainWindow>();
        services.AddSingleton<IConfirmationService>(p => new AvaloniaConfirmationService(() => p.GetRequiredService<MainWindow>()));
        services.AddSingleton<IDialogService>(p => new AvaloniaDialogService(() => p.GetRequiredService<MainWindow>()));
        services.AddSingleton<IAppLifetime>(_ => new AvaloniaAppLifetime(desktop));
        services.AddSingleton(_ => new BuildHistoryStore(layout));

        services.AddSingleton<BuildCoordinator>();

        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<BuildPanelViewModel>();
        services.AddSingleton<ProfilePickerViewModel>();
        services.AddSingleton<AppPickerViewModel>();
        services.AddSingleton<SubAppWorkspaceViewModel>();
        services.AddSingleton<EpicBuildPanelViewModel>();
        services.AddSingleton<EpicWorkspaceViewModel>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}
