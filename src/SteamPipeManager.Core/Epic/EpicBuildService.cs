using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Epic;

/// <summary>Tek bir Epic yayınlama isteğinin bağlamı.</summary>
public sealed record EpicBuildRequest(
    UserProfile Profile,
    SteamApp App,
    EpicArtifact Artifact,
    EpicCredentials Credentials)
{
    /// <summary>
    /// Kullanılacak sürüm. Boş bırakılırsa şablondan, geçmişe bakılarak üretilir.
    /// </summary>
    public string? BuildVersion { get; init; }

    /// <summary>Hedefin kendi ayarına bakmadan kuru çalıştırmaya zorlar.</summary>
    public bool PreviewOverride { get; init; }
}

public sealed record EpicBuildOutcome(
    BuildRecord Record,
    IReadOnlyList<BptEvent> Events,
    string? BlockedReason = null)
{
    public bool Started => BlockedReason is null;

    /// <summary>
    /// Yükleme başarılı oldu ama etiketleme başarısız: build Epic'te <b>duruyor</b> ama
    /// canlı değil. Kullanıcıya bunu söylemek şart — aksi hâlde ya build'in kaybolduğunu
    /// sanıp baştan yükler ya da canlı olduğunu sanır.
    /// </summary>
    public bool UploadedButNotLabelled { get; init; }
}

/// <summary>
/// Epic'e yayınlama akışı.
///
/// Steam'den yapıca en büyük farkı burada: Steam'de canlıya alma build script'inin
/// içindeki <c>setlive</c> alanı, yani <b>tek çağrı</b>. Epic'te ise önce
/// <c>UploadBinary</c>, sonra ayrı bir <c>LabelBinary</c> çağrısı gerekiyor. Aradaki
/// başarısızlık gerçek bir durum: yükleme tamam ama etiket uygulanamamış olabilir.
///
/// Aynı anda yalnızca bir build çalışıyor — BPT çalıştırmaları aynı geçici alanı ve
/// aynı kimlik oturumunu paylaşıyor.
/// </summary>
public sealed class EpicBuildService(BptInstallation installation, string workspaceDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BptInstallation Installation { get; } = installation;

    public bool IsBuilding { get; private set; }

    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Yeni bir sürüm üretirken kaçınılacak sürümler; normalde build geçmişinden gelir.
    /// </summary>
    public Func<EpicArtifact, IReadOnlyCollection<string>>? KnownVersions { get; set; }

    public async Task<EpicBuildOutcome> BuildAsync(
        EpicBuildRequest request,
        IProgress<BptEvent>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _gate.WaitAsync(0, ct))
        {
            return Blocked(request, Loc.T("Build.AlreadyRunning"));
        }

        IsBuilding = true;

        try
        {
            return await RunAsync(request, progress, ct);
        }
        finally
        {
            IsBuilding = false;
            _gate.Release();
        }
    }

    private async Task<EpicBuildOutcome> RunAsync(
        EpicBuildRequest request,
        IProgress<BptEvent>? progress,
        CancellationToken ct)
    {
        var artifact = request.Artifact;

        if (!Installation.Exists)
        {
            return Blocked(request, Loc.T("Epic.ToolNotFoundAt", Installation.ExecutablePath));
        }

        var validation = EpicArtifactValidator.Validate(
            artifact, request.Profile.Epic, request.App.Epic);

        if (!validation.CanBuild)
        {
            return Blocked(request, string.Join(" ", validation.Issues.Select(i => i.Message)));
        }

        var version = request.BuildVersion is { Length: > 0 } chosen
            ? chosen
            : NextVersion(request);

        var versionCheck = EpicBuildVersion.Validate(version);

        if (!versionCheck.CanSave)
        {
            return Blocked(request, string.Join(" ", versionCheck.Issues.Select(i => i.Message)));
        }

        var preview = artifact.Preview || request.PreviewOverride;
        var record = NewRecord(request, version, preview);
        var events = new List<BptEvent>();

        var runner = new BptRunner(Installation)
        {
            StallTimeout = StallTimeout,
            TotalBytes = MeasureContent(artifact.BuildRoot),
        };

        // --- 1. adım: yükleme ---
        var uploadLog = LogPath(request, version, "upload");
        var upload = await runner.RunAsync(
            UploadCommand(request, version, preview), uploadLog, progress, ct);

        events.AddRange(upload.Events);
        record.LogFilePath = uploadLog;
        record.ExitCode = upload.ExitCode;

        if (upload.Cancelled)
        {
            return Finish(record, events, BuildOutcome.Cancelled, Loc.T("Build.CancelledByUser"));
        }

        if (!upload.Succeeded)
        {
            return Finish(record, events, BuildOutcome.Failed, DescribeFailure(upload));
        }

        // Kuru çalıştırmada yüklenen bir şey yok, dolayısıyla etiketlenecek de bir şey yok.
        if (preview)
        {
            return Finish(record, events, BuildOutcome.Succeeded, null);
        }

        // --- 2. adım: canlıya alma ---
        if (artifact.Label is not { Length: > 0 })
        {
            return Finish(record, events, BuildOutcome.Succeeded, null);
        }

        var labelLog = LogPath(request, version, "label");
        var label = await runner.RunAsync(LabelCommand(request, version), labelLog, progress, ct);

        events.AddRange(label.Events);

        if (label.Succeeded)
        {
            return Finish(record, events, BuildOutcome.Succeeded, null);
        }

        // Yükleme tamam, etiketleme değil. Build Epic'te duruyor ama canlı değil;
        // kullanıcı yalnızca etiketlemeyi tekrar deneyebilir, baştan yüklemesi gerekmiyor.
        var outcome = Finish(
            record,
            events,
            BuildOutcome.Failed,
            Loc.T("Epic.UploadedButNotLabelled", version, artifact.Label, DescribeFailure(label)));

        return outcome with { UploadedButNotLabelled = true };
    }

    // --- Komutlar ---

    private static BptCommand UploadCommand(EpicBuildRequest request, string version, bool preview)
    {
        var artifact = request.Artifact;

        var command = new BptCommand("UploadBinary")
            .WithCredentials(
                request.Credentials.OrganizationId,
                request.Credentials.ProductId,
                request.Credentials.ArtifactId,
                request.Credentials.ClientId,
                request.Credentials.ClientSecret)
            .Add("BuildRoot", artifact.BuildRoot)
            .Add("BuildVersion", version)
            .Add("AppLaunch", artifact.AppLaunch)
            .Add("AppArgs", artifact.AppArgs)
            .Add("CloudDir", artifact.CloudDir);

        return preview ? command.AddFlag("DryRun") : command;
    }

    private static BptCommand LabelCommand(EpicBuildRequest request, string version) =>
        new BptCommand("LabelBinary")
            .WithCredentials(
                request.Credentials.OrganizationId,
                request.Credentials.ProductId,
                request.Credentials.ArtifactId,
                request.Credentials.ClientId,
                request.Credentials.ClientSecret)
            .Add("BuildVersion", version)
            .Add("Label", request.Artifact.Label)
            .Add("Platform", request.Artifact.Platform)
            .Add("SandboxId", request.Artifact.SandboxId);

    // --- Yardımcılar ---

    private string NextVersion(EpicBuildRequest request) =>
        EpicBuildVersion.NextUnique(
            request.Artifact.BuildVersionTemplate,
            KnownVersions?.Invoke(request.Artifact) ?? [],
            request.App.Title,
            request.Artifact.Title,
            request.Artifact.Kind,
            request.Artifact.ArtifactId);

    /// <summary>
    /// İlerlemeyi yüzdeye çevirebilmek için içeriğin toplam boyutu. Araç yüzde
    /// bildirmiyor, yalnızca taranan bayt konumunu.
    /// </summary>
    private static long? MeasureContent(string buildRoot)
    {
        try
        {
            return Directory.Exists(buildRoot)
                ? Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ölçemezsek ilerleme yüzdesiz gösterilir; build'i engellemeye değmez.
            return null;
        }
    }

    private string LogPath(EpicBuildRequest request, string version, string step)
    {
        var safeVersion = EpicBuildVersion.Normalize(version);

        return Path.Combine(
            workspaceDirectory,
            request.Profile.Id.ToString("N"),
            request.App.Id.ToString("N"),
            request.Artifact.Id.ToString("N"),
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safeVersion}-{step}.log");
    }

    private static string DescribeFailure(BptRunResult result)
    {
        if (result.TimedOut)
        {
            return Loc.T("Build.Stalled");
        }

        if (result.Events.FirstOrDefault(e => e.Kind == BptEventKind.AuthenticationFailed) is not null)
        {
            return Loc.T("Epic.CredentialsRejected");
        }

        if (result.Failure is { } failure)
        {
            return failure.ExitReason is { Length: > 0 } reason
                ? Loc.T("Epic.ToolExited", reason)
                : failure.Message;
        }

        return Loc.T("Build.NotFinished", result.ExitCode);
    }

    private static BuildRecord NewRecord(EpicBuildRequest request, string version, bool preview) =>
        new()
        {
            SubAppId = request.Artifact.Id,
            StartedAt = DateTimeOffset.Now,
            Outcome = BuildOutcome.Running,
            WasPreview = preview,
            Description = version,
            Provider = PublishProviderId.Epic,
            EpicArtifactId = request.Artifact.ArtifactId,
            EpicBuildVersion = version,
            SetLiveBranch = request.Artifact.Label,
        };

    private static EpicBuildOutcome Finish(
        BuildRecord record,
        List<BptEvent> events,
        BuildOutcome outcome,
        string? failureReason)
    {
        record.FinishedAt = DateTimeOffset.Now;
        record.Outcome = outcome;
        record.FailureReason = failureReason;

        return new EpicBuildOutcome(record, events);
    }

    private static EpicBuildOutcome Blocked(EpicBuildRequest request, string reason) =>
        new(NewRecord(request, request.BuildVersion ?? "", false), [], reason);
}
