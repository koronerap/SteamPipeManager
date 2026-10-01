using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Updates;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Presentation.ViewModels;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    ManualOnly,
    Downloading,
    Installing,
    Restarting,
    Failed,

    /// <summary>Bu açılış bir güncellemenin ardından yapıldı.</summary>
    Updated,
}

/// <summary>
/// Uygulama içi güncelleme: sor, indir, doğrula, dosyaları değiştir, yeniden başlat.
///
/// Hiçbir adım kullanıcı düğmeye basmadan kurulum yapmıyor; açılıştaki kontrol
/// yalnızca şeridi gösteriyor. Build sürerken kurulum başlatılmıyor — yeniden
/// başlatma SteamCMD/BuildPatchTool'u yarıda keserdi.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly ISettingsStore _settings;
    private readonly WorkspaceLayout _layout;
    private readonly ProductProfile _product;
    private readonly BuildCoordinator _coordinator;
    private readonly EpicBuildPanelViewModel _epicBuild;
    private readonly SubAppWorkspaceViewModel _workspace;
    private readonly EpicWorkspaceViewModel _epicWorkspace;
    private readonly GitHubReleaseClient _client;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly IAppLifetime _lifetime;

    private UpdateCheckResult? _result;
    private AppVersion? _updatedFrom;
    private string _detail = "";
    private bool _failedWhileInstalling;

    public UpdateViewModel(
        ISettingsStore settings,
        WorkspaceLayout layout,
        ProductProfile product,
        BuildCoordinator coordinator,
        EpicBuildPanelViewModel epicBuild,
        SubAppWorkspaceViewModel workspace,
        EpicWorkspaceViewModel epicWorkspace,
        IAppLifetime lifetime)
    {
        _lifetime = lifetime;
        _settings = settings;
        _layout = layout;
        _product = product;
        _coordinator = coordinator;
        _epicBuild = epicBuild;
        _workspace = workspace;
        _epicWorkspace = epicWorkspace;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client = new GitHubReleaseClient(http, UpdateSource.Resolve());
        _target = UpdateTarget.For(product);
        _checker = new UpdateChecker(_client, _target);

        // İndirme ayrı bir istemciyle: 30 saniyelik zaman aşımı yavaş bağlantıda
        // 70 MB'lık paketi kesmesin; üst sınır iptal belirteciyle konuyor.
        _downloader = new UpdateDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, _client);

        Current = ReadCurrentVersion();

        AppLocalizer.Instance.PropertyChanged += (_, _) => NotifyTexts();
    }

    public AppVersion Current { get; }

    public string InstalledVersionText => AppLocalizer.Instance.Format("Settings.Updates.Current", Current);

    /// <summary>Bu ürünün bu platformdaki paketi ve paketin içindeki düzen.</summary>
    private readonly UpdateTarget _target;

    /// <summary>Uygulama kökünden çalıştırılan dosyaya göreli yol; macOS'ta .app içinde.</summary>
    private string ExecutableName => _target.ExecutableRelativePath;

    private static string? RunningExecutable => Environment.ProcessPath;

    private string? LatestText => _result?.Release?.Version.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible), nameof(CanInstall), nameof(IsWorking),
        nameof(ShowProgress), nameof(IsProgressIndeterminate), nameof(CanOpenReleasePage), nameof(CanDismiss),
        nameof(BannerText), nameof(StatusText), nameof(ReleasePageLabel))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallCommand))]
    private UpdateStage _stage = UpdateStage.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(StatusText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private bool _isDismissed;

    /// <summary>Şeridin ikinci satırı: ör. "build sürüyor, bitince güncelleyin".</summary>
    [ObservableProperty]
    private string _note = "";

    public bool IsWorking => Stage is UpdateStage.Checking or UpdateStage.Downloading
        or UpdateStage.Installing or UpdateStage.Restarting;

    public bool CanInstall => Stage == UpdateStage.Available;

    public bool ShowProgress => Stage is UpdateStage.Downloading or UpdateStage.Installing;

    public bool IsProgressIndeterminate => Stage == UpdateStage.Installing;

    public bool CanOpenReleasePage => Stage is UpdateStage.Available or UpdateStage.ManualOnly
        or UpdateStage.Updated or UpdateStage.Failed;

    public bool CanDismiss => !IsWorking;

    public bool IsBannerVisible => !IsDismissed && Stage switch
    {
        UpdateStage.Available or UpdateStage.ManualOnly or UpdateStage.Downloading or
            UpdateStage.Installing or UpdateStage.Restarting or UpdateStage.Updated => true,
        UpdateStage.Failed => _failedWhileInstalling,
        _ => false,
    };

    public string ReleasePageLabel => AppLocalizer.Instance.Get(
        Stage is UpdateStage.Available or UpdateStage.Updated ? "Update.ReleaseNotes" : "Update.OpenDownloadPage");

    public string BannerText => Stage switch
    {
        UpdateStage.Available => AppLocalizer.Instance.Format("Update.Banner.Available", LatestText ?? "", Current),
        UpdateStage.ManualOnly => AppLocalizer.Instance.Format("Update.Banner.ManualOnly", LatestText ?? "", _detail),
        UpdateStage.Downloading => AppLocalizer.Instance.Format("Update.Banner.Downloading", LatestText ?? "",
            (int)Math.Round(Progress * 100)),
        UpdateStage.Installing => AppLocalizer.Instance.Format("Update.Banner.Installing", LatestText ?? ""),
        UpdateStage.Restarting => AppLocalizer.Instance.Get("Update.Banner.Restarting"),
        UpdateStage.Updated => AppLocalizer.Instance.Format("Update.Banner.Updated", Current,
            _updatedFrom?.ToString() ?? ""),
        UpdateStage.Failed => AppLocalizer.Instance.Format(
            _failedWhileInstalling ? "Update.Banner.Failed" : "Update.CheckFailed", _detail),
        _ => "",
    };

    /// <summary>Ayarlar kartındaki durum satırı.</summary>
    public string StatusText => Stage switch
    {
        UpdateStage.Checking => AppLocalizer.Instance.Get("Update.Checking"),
        UpdateStage.UpToDate => AppLocalizer.Instance.Format("Update.UpToDate", Current),
        UpdateStage.Idle => "",
        _ => BannerText,
    };

    /// <summary>
    /// Pencere açıldıktan sonra çağrılır: güncelleme sonrası açılışsa bunu bildirir ve
    /// eski dosyaları temizler; ayar açıksa yeni sürümü sorar.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<string> args)
    {
        var launch = UpdateLaunchArguments.Parse(args);

        if (launch.UpdatedFrom is { } from)
        {
            _updatedFrom = from;
            Stage = UpdateStage.Updated;
        }

        _ = Task.Run(() => CleanupAsync(launch.WaitForProcessId));

        if (launch.UpdatedFrom is not null)
        {
            // Az önce güncellendi; aynı açılışta bir daha sormak gereksiz.
            return;
        }

        try
        {
            if (!(await _settings.LoadAsync()).CheckForUpdates)
            {
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        await CheckAsync(silent: true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(silent: false);

    private bool CanCheck() => !IsWorking;

    /// <param name="silent">
    /// Açılıştaki kontrol: ağ yoksa ya da GitHub yanıt vermezse kullanıcı rahatsız edilmez.
    /// </param>
    private async Task CheckAsync(bool silent)
    {
        if (IsWorking)
        {
            return;
        }

        Note = "";
        _failedWhileInstalling = false;

        if (!silent)
        {
            IsDismissed = false;
        }

        Stage = UpdateStage.Checking;

        try
        {
            _result = await _checker.CheckAsync(Current);

            switch (_result.Availability)
            {
                case UpdateAvailability.UpToDate:
                    Stage = silent ? UpdateStage.Idle : UpdateStage.UpToDate;
                    break;

                case UpdateAvailability.ManualOnly:
                    _detail = AppLocalizer.Instance.Get("Update.Reason.NoPackage");
                    Stage = UpdateStage.ManualOnly;
                    break;

                case UpdateAvailability.Available:
                    ApplyReadiness();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (silent)
            {
                Stage = UpdateStage.Idle;
                return;
            }

            _detail = Describe(ex);
            Stage = UpdateStage.Failed;
        }
    }

    /// <summary>Paket kurulabilir olsa da bu kopya yerinde güncellenebilir mi.</summary>
    private void ApplyReadiness()
    {
        switch (UpdateInstaller.CheckReadiness(RunningExecutable, _target))
        {
            case InstallReadiness.Ready:
                Stage = UpdateStage.Available;
                break;

            case InstallReadiness.DevelopmentBuild:
                _detail = AppLocalizer.Instance.Get("Update.Reason.DevelopmentBuild");
                Stage = UpdateStage.ManualOnly;
                break;

            case InstallReadiness.NotWritable:
                _detail = AppLocalizer.Instance.Get("Update.Reason.NotWritable");
                Stage = UpdateStage.ManualOnly;
                break;
        }
    }

    private bool IsBuildRunning => _coordinator.IsBuilding || _epicBuild.IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_result is not { Availability: UpdateAvailability.Available } update)
        {
            return;
        }

        if (IsBuildRunning)
        {
            Note = AppLocalizer.Instance.Get("Update.BlockedByBuild");
            return;
        }

        ApplyReadiness();

        if (Stage != UpdateStage.Available ||
            RunningExecutable is not { } executable ||
            _target.AppRootOf(executable) is not { } appDirectory)
        {
            return;
        }

        Note = "";
        Progress = 0;
        Stage = UpdateStage.Downloading;

        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);

            var package = await _downloader.DownloadAsync(
                update,
                _layout.UpdatesDirectory,
                new Progress<double>(value => Progress = value),
                timeout.Token);

            Stage = UpdateStage.Installing;

            var staged = await Task.Run(() => UpdateInstaller.Stage(
                package, Path.Combine(_layout.UpdatesDirectory, "staged"), ExecutableName));

            // İndirme sürerken bir build başlatılmış olabilir.
            if (IsBuildRunning)
            {
                Note = AppLocalizer.Instance.Get("Update.BlockedByBuild");
                Stage = UpdateStage.Available;
                return;
            }

            // Otomatik kayıt gecikmeli yazıyor; yeniden başlamadan bekleyenler diske geçsin.
            await _workspace.FlushAsync();
            await _epicWorkspace.FlushAsync();

            var installed = await Task.Run(() => UpdateInstaller.Install(staged, appDirectory));

            Stage = UpdateStage.Restarting;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = UpdateLaunchArguments.Build(Current, Environment.ProcessId),
                    WorkingDirectory = appDirectory,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                installed.Rollback();
                throw new UpdateException(UpdateFailure.RestartFailed, ex.Message, ex);
            }

            _lifetime.Shutdown();
        }
        catch (Exception ex)
        {
            _detail = Describe(ex);
            _failedWhileInstalling = true;
            IsDismissed = false;
            Stage = UpdateStage.Failed;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _result?.Release?.PageUrl ?? _client.Source.ReleasesPageUrl;

        Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
    }

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;

    /// <summary>
    /// Önceki sürümün kenara alınmış dosyalarını siler. Eski süreç kapanana kadar
    /// dosyalar kilitli olduğu için önce onun çıkması bekleniyor.
    /// </summary>
    private async Task CleanupAsync(int? previousProcessId)
    {
        try
        {
            if (previousProcessId is { } pid)
            {
                try
                {
                    using var previous = Process.GetProcessById(pid);
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await previous.WaitForExitAsync(wait.Token);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
                {
                    // Süreç zaten kapanmış ya da takılmış; silinemeyenler sonraki açılışa kalır.
                }
            }

            if (UpdateInstaller.CheckReadiness(RunningExecutable, _target) == InstallReadiness.Ready &&
                _target.AppRootOf(RunningExecutable) is { } appDirectory)
            {
                for (var attempt = 0; attempt < 5 && UpdateInstaller.CleanupLeftovers(appDirectory) > 0; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            if (Directory.Exists(_layout.UpdatesDirectory))
            {
                Directory.Delete(_layout.UpdatesDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Temizlik en iyi çaba; başarısızlığı kullanıcıyı ilgilendirmiyor.
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UpdateException update => AppLocalizer.Instance.Get("Update.Error." + update.Failure),
        OperationCanceledException => AppLocalizer.Instance.Get("Update.Error.Network"),
        _ => AppLocalizer.Instance.Format("Update.Error.Unexpected", ex.Message),
    };

    private void NotifyTexts()
    {
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ReleasePageLabel));
        OnPropertyChanged(nameof(InstalledVersionText));
    }

    /// <summary>
    /// Derlemenin sürümü. Bilgi sürümü "1.0.2+abc123" gibi kaynak kimliği taşıyabilir;
    /// ayrıştırıcı onu yok sayıyor.
    /// </summary>
    private static AppVersion ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateViewModel).Assembly;

        return AppVersion.TryParse(
                   assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
               ?? AppVersion.TryParse(assembly.GetName().Version?.ToString())
               ?? new AppVersion(0, 0, 0);
    }
}
