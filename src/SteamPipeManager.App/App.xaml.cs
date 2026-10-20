using System.Reflection;
// WPF'in örtük using'leri System.IO'yu kapsamıyor.
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SteamPipeManager.App.Localization;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;
using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var layout = WorkspaceLayout.Default();

        try
        {
            await StartAsync(layout, e.Args);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(layout, ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Açılışta bir şey patlarsa pencere hiç görünmeden uygulama kayboluyordu ve kullanıcı
    /// yalnızca "açılmıyor" görüyordu (geçersiz bir simge adı böyle yaptı). Ayrıntı log
    /// klasörüne yazılıyor, kullanıcıya nedeni ve dosyanın yeri söyleniyor.
    /// </summary>
    internal static void ReportStartupFailure(WorkspaceLayout layout, Exception ex)
    {
        string? logPath = null;

        try
        {
            Directory.CreateDirectory(layout.LogsDirectory);
            logPath = Path.Combine(layout.LogsDirectory, $"startup-error-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(logPath, ex.ToString());
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Log yazılamasa da kullanıcıya neden söyleniyor.
        }

        var reason = ex.GetBaseException().Message;
        var product = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Steam Pipe Manager";

        // Dil dosyaları yüklenmeden de patlayabilir; o zaman İngilizce.
        var message = AppLocalizer.Instance.Get("Startup.Failed") is { } template && template != "Startup.Failed"
            ? AppLocalizer.Instance.Format("Startup.Failed", product, reason, logPath ?? "—")
            : $"{product} could not start: {reason}\n\nThe details were saved to:\n{logPath ?? "—"}";

        MessageBox.Show(message, product, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task StartAsync(WorkspaceLayout layout, string[] args)
    {
        layout.EnsureCreated();

        await InitializeLanguageAsync(layout);

        _services = BuildServices(layout);

        var shell = _services.GetRequiredService<ShellViewModel>();

        var setupCompleted = await IsSetupCompletedAsync(layout);

        try
        {
            await shell.InitializeAsync(setupCompleted);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Kayıtlı profiller yüklenemedi:\n\n{ex.Message}",
                "Steam Pipe Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var window = _services.GetRequiredService<MainWindow>();
        window.DataContext = shell;
        window.Show();

        // Pencere açıldıktan sonra: ağ beklemesi açılışı geciktirmesin.
        _ = shell.Updates.StartAsync(args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Dil dosyalarını hazırlar. İlk çalıştırmada (ayarda dil yoksa) sistem diline göre
    /// seçilir; eşleşme yoksa İngilizce kullanılır ve seçim ayarlara yazılır.
    /// </summary>
    private static async Task InitializeLanguageAsync(WorkspaceLayout layout)
    {
        AppLocalizer.Instance.Initialize(EmbeddedLanguages.Read(), layout.LanguageDirectory);

        var store = new JsonSettingsStore(layout);

        try
        {
            var settings = await store.LoadAsync();

            if (settings.Language is { Length: > 0 } saved)
            {
                AppLocalizer.Instance.Use(saved);
                return;
            }

            var detected = AppLocalizer.Instance.ResolveSystemLanguage();
            AppLocalizer.Instance.Use(detected);

            settings.Language = detected;
            await store.SaveAsync(settings);
        }
        catch (Exception)
        {
            // Ayarlar okunamazsa varsayılan dille devam edilir.
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

    private static ServiceProvider BuildServices(WorkspaceLayout layout)
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
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SpmProduct")?.Value));

        // Epic client secret'ları profil dosyasının dışında, DPAPI ile şifreli duruyor.
        services.AddSingleton(_ => new EpicSecretStore(layout.EpicSecretsFile));
        services.AddSingleton<IConfirmationService, MessageBoxConfirmationService>();
        services.AddSingleton<IDialogService, WindowsDialogService>();
        services.AddSingleton<IAppLifetime, WpfAppLifetime>();
        services.AddSingleton(_ => new BuildHistoryStore(layout));

        // Analitik: oyuncu sayısı, wishlist ve satışlar. Widget da aynı parçaları kuruyor.
        var analytics = AnalyticsServices.Create(layout);
        services.AddSingleton(analytics);
        services.AddSingleton(analytics.Service);
        services.AddSingleton(analytics.Keys);
        // Finans anahtarları profil başına: her oyun kendi profilinin anahtarıyla sorgulanıyor.
        services.AddSingleton(p => new AnalyticsRefresher(
            analytics.Service,
            () => AnalyticsPlan.Build(AnalyticsTargets.From(p.GetRequiredService<ProfileRepository>().Profiles), analytics.Keys.Read).Scope));
        services.AddSingleton<IWidgetLauncher, WpfWidgetLauncher>();
        services.AddSingleton<AnalyticsViewModel>();

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

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
