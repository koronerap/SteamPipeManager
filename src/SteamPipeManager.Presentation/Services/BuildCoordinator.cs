using System.IO;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Presentation.Services;

/// <summary>
/// SteamCMD kurulumunu bulur/indirir ve build ile oturum işlemlerini tek kapıdan sunar.
/// ViewModel'lerin kurulum yolu, indirme veya bootstrap ayrıntılarıyla uğraşmasını önler.
/// </summary>
public sealed class BuildCoordinator(
    WorkspaceLayout layout,
    ISettingsStore settingsStore,
    SteamCmdProvisioner provisioner,
    EpicSecretStore secrets)
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

    /// <summary>
    /// Profilin yayın yapmaya hazır olup olmadığını sorar. Soru iki sağlayıcıda da aynı,
    /// cevabın bulunma yolu farklı: Steam'de SteamCMD'nin önbelleklediği oturum,
    /// Epic'te <c>UploadBinary -DryRun</c> ile kimlik doğrulaması.
    /// </summary>
    public async Task<SessionCheckResult> CheckSessionAsync(
        UserProfile profile,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        if (profile.Provider == PublishProviderId.Epic)
        {
            return await CheckEpicSessionAsync(profile, status, ct);
        }

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

    /// <summary>
    /// Epic kimlik kontrolü. Araç yoksa ya da secret kayıtlı değilse kontrol
    /// başarısız sayılıyor — kimlik reddedildi demek değil, ikisini karıştırmak
    /// kullanıcıyı boşuna kimlik bilgisi girmeye yönlendirir.
    /// </summary>
    private async Task<SessionCheckResult> CheckEpicSessionAsync(
        UserProfile profile,
        IProgress<string>? status,
        CancellationToken ct)
    {
        if (profile.Epic is not { IsComplete: true } epic)
        {
            return new SessionCheckResult(
                SessionState.CheckFailed, AppLocalizer.Instance.Get("Epic.Validate.ProfileIncomplete"));
        }

        var settings = await settingsStore.LoadAsync(ct);

        if (BptInstallation.LocateExecutable(settings.BuildPatchToolPath ?? "") is not { } exe)
        {
            return new SessionCheckResult(
                SessionState.CheckFailed, AppLocalizer.Instance.Get("Epic.ToolPathMissing"));
        }

        if (secrets.Read(profile.Id) is not { Length: > 0 } secret)
        {
            return new SessionCheckResult(
                SessionState.LoginRequired, AppLocalizer.Instance.Get("Epic.SecretMissing"));
        }

        // Epic'te kimlik bilgileri tek başına doğrulanamıyor: BuildPatchTool argüman
        // doğrulamasını kimlik doğrulamasından önce yapıyor, yani ProductId ve
        // ArtifactId olmadan kimliğe hiç sıra gelmiyor. Steam'de oturum hesap
        // düzeyindeydi; burada kontrol ancak gerçek bir hedef üzerinden yapılabiliyor.
        if (FirstEpicTarget(profile) is not { } target)
        {
            return new SessionCheckResult(
                SessionState.Unknown, AppLocalizer.Instance.Get("Epic.NoTargetToCheck"));
        }

        var (productId, artifactId) = target;

        status?.Report(AppLocalizer.Instance.Get("Session.Checking"));

        var service = new BptSessionService(new BptInstallation(exe))
        {
            CheckTimeout = TimeSpan.FromSeconds(settings.SessionCheckTimeoutSeconds),
        };

        var logPath = Path.Combine(
            layout.LogsDirectory, "epic", $"check-{profile.Id:N}.log");

        return await service.CheckAsync(
            new EpicCredentials(epic.OrganizationId, productId, artifactId, epic.ClientId, secret),
            logPath,
            ct: ct);
    }

    /// <summary>
    /// Kontrol için kullanılacak ilk tam tanımlı hedef. Bulunamazsa null — profilde
    /// henüz oyun/artifact yok demektir ve kimlik doğrulanamaz.
    /// </summary>
    private static (string ProductId, string ArtifactId)? FirstEpicTarget(UserProfile profile)
    {
        foreach (var app in profile.Apps)
        {
            if (app.Epic is not { ProductId: { Length: > 0 } productId } game)
            {
                continue;
            }

            foreach (var artifact in game.Artifacts)
            {
                if (artifact.ArtifactId is { Length: > 0 } artifactId)
                {
                    return (productId, artifactId);
                }
            }
        }

        return null;
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
