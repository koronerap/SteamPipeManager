using System.Reflection;
// WPF'in örtük using'leri System.IO'yu kapsamıyor.
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SteamPipeManager.App.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.App.ViewModels;
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
        AppLocalizer.Instance.Initialize(ReadEmbeddedLanguages(), layout.LanguageDirectory);

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

    /// <summary>
    /// Yerleşik dil dosyalarını exe'nin içinden okur. Tek dosya dağıtımda yanında
    /// klasör taşınmasın diye gömülü kaynak olarak paketleniyorlar.
    /// </summary>
    private static Dictionary<string, string> ReadEmbeddedLanguages()
    {
        var assembly = typeof(App).Assembly;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains(".lang.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);

            // Kaynak adı "SteamPipeManager.App.lang.en.json" biçiminde; dosya adına indiriyoruz.
            var fileName = name[(name.IndexOf(".lang.", StringComparison.OrdinalIgnoreCase) + 6)..];
            result[fileName] = reader.ReadToEnd();
        }

        return result;
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
        services.AddSingleton(_ => new BuildHistoryStore(layout));

        services.AddSingleton<BuildCoordinator>();

        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<BuildPanelViewModel>();
        services.AddSingleton<ProfilePickerViewModel>();
        services.AddSingleton<AppPickerViewModel>();
        services.AddSingleton<SubAppWorkspaceViewModel>();
        services.AddSingleton<EpicBuildPanelViewModel>();
        services.AddSingleton<EpicWorkspaceViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
