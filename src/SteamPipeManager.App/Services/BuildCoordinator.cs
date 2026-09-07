using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.App.Services;

/// <summary>
/// SteamCMD kurulumunu bulur/indirir ve build ile oturum işlemlerini tek kapıdan sunar.
/// ViewModel'lerin kurulum yolu, indirme veya bootstrap ayrıntılarıyla uğraşmasını önler.
/// </summary>
public sealed class BuildCoordinator(
    WorkspaceLayout layout,
    ISettingsStore settingsStore,
    SteamCmdProvisioner provisioner)
{
    private SteamCmdInstallation? _installation;

    public bool IsBuilding => _buildService?.IsBuilding ?? false;

    private BuildService? _buildService;

    /// <summary>
    /// SteamCMD zaten hazır mı — indirme başlatmadan sorar.
    /// Açılışta oturum kontrolü yapılıp yapılmayacağına buna bakarak karar veriliyor.
    /// </summary>
    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        if (_installation is { Exists: true })
        {
            return true;
        }

        var settings = await settingsStore.LoadAsync(ct);

        var path = settings.SteamCmdPath is { Length: > 0 } configured
            ? configured
            : layout.ManagedSteamCmdDirectory;

        return SteamCmdProvisioner.LocateExecutable(path) is not null;
    }

    /// <summary>
    /// Ayarlarda bir yol varsa onu kullanır; yoksa uygulamanın kendi kurulumunu indirir.
    /// Bootstrap (exit 7 → ikinci çalıştırma) burada tamamlanır.
    /// </summary>
    public async Task<SteamCmdInstallation> EnsureInstallationAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        if (_installation is { Exists: true })
        {
            return _installation;
        }

        var settings = await settingsStore.LoadAsync(ct);

        if (settings.SteamCmdPath is { Length: > 0 } configured &&
            SteamCmdProvisioner.LocateExecutable(configured) is { } located)
        {
            _installation = new SteamCmdInstallation(located);
            return _installation;
        }

        status?.Report("SteamCMD hazırlanıyor…");

        var progress = new Progress<ProvisionProgress>(p => status?.Report(
            p.Fraction is { } f ? $"{p.Stage} ({f:P0})" : p.Stage));

        var exePath = await provisioner.EnsureInstalledAsync(
            layout.ManagedSteamCmdDirectory, progress, ct);

        _installation = new SteamCmdInstallation(exePath);

        status?.Report("SteamCMD ilk kez başlatılıyor (kendini güncelliyor)…");
        await new SteamCmdSessionService(_installation).BootstrapAsync(ct: ct);

        settings.SteamCmdPath = exePath;
        settings.SetupCompleted = true;
        await settingsStore.SaveAsync(settings, ct);

        return _installation;
    }

    public async Task<SessionCheckResult> CheckSessionAsync(
        UserProfile profile,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var installation = await EnsureInstallationAsync(status, ct);
        var settings = await settingsStore.LoadAsync(ct);

        var service = new SteamCmdSessionService(installation)
        {
            CheckTimeout = TimeSpan.FromSeconds(settings.SessionCheckTimeoutSeconds),
        };

        status?.Report("Oturum kontrol ediliyor…");
        return await service.CheckAsync(profile.SteamUsername, ct);
    }

    /// <summary>
    /// Uygulama içi giriş: şifre stdin üzerinden SteamCMD'ye iletilir, konsol penceresi
    /// açılmaz. Steam Guard kodu gerekirse <paramref name="guardCodeProvider"/> çağrılır.
    /// </summary>
    public async Task<LoginResult> LoginAsync(
        UserProfile profile,
        string password,
        Func<CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<LoginProgress>? progress = null,
        CancellationToken ct = default)
    {
        var installation = await EnsureInstallationAsync(ct: ct);

        var session = new SteamCmdLoginSession(installation)
        {
            GuardCodeProvider = guardCodeProvider,
        };

        return await session.LoginAsync(profile.SteamUsername, password, progress, ct);
    }

    /// <summary>
    /// Yedek yol: SteamCMD'yi kendi konsol penceresinde açar. Uygulama içi giriş
    /// beklenmedik bir durumda çalışmazsa buraya düşülebilir.
    /// </summary>
    public async Task<SessionCheckResult> InteractiveLoginAsync(
        UserProfile profile,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var installation = await EnsureInstallationAsync(status, ct);

        status?.Report("SteamCMD giriş penceresi açıldı — şifreni oraya yaz.");

        using var process = new SteamCmdSessionService(installation)
            .StartInteractiveLogin(profile.SteamUsername);

        await process.WaitForExitAsync(ct);

        return await CheckSessionAsync(profile, status, ct);
    }

    public async Task<BuildOutcomeResult> BuildAsync(
        BuildRequest request,
        IProgress<SteamCmdEvent>? progress = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var installation = await EnsureInstallationAsync(status, ct);
        var settings = await settingsStore.LoadAsync(ct);

        _buildService ??= new BuildService(layout, installation);
        _buildService.StallTimeout = TimeSpan.FromSeconds(settings.StallWarningSeconds);

        return await _buildService.BuildAsync(request, progress, ct);
    }
}
