using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Vdf;
using SteamPipeManager.Core.Workspace;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// Bir build isteğinin bağlamı. <paramref name="DepotIds"/> verilirse yalnızca o depolar
/// gönderilir; null ise hedefin bütün depoları.
/// </summary>
public sealed record BuildRequest(
    UserProfile Profile,
    SteamApp App,
    SubApp SubApp,
    string Description,
    bool PreviewOverride = false,
    IReadOnlyCollection<uint>? DepotIds = null)
{
    /// <summary>Bu build'de gönderilecek depolar, hedefteki sırasıyla.</summary>
    public IReadOnlyList<DepotConfig> SelectedDepots =>
        DepotIds is null
            ? [.. SubApp.Depots]
            : [.. SubApp.Depots.Where(d => DepotIds.Contains(d.DepotId))];

    /// <summary>Hedefin depolarından bazıları bilerek dışarıda bırakıldı mı.</summary>
    public bool IsPartial => SelectedDepots.Count < SubApp.Depots.Count;
}

public sealed record BuildOutcomeResult(
    BuildRecord Record,
    IReadOnlyList<SteamCmdEvent> Events,
    string? BlockedReason = null)
{
    public bool Started => BlockedReason is null;
}

/// <summary>
/// Script üretimi, oturum kontrolü ve SteamCMD çalıştırmasını tek akışta birleştirir.
/// Aynı anda yalnızca bir build çalışabilir (v1.0 kararı).
/// </summary>
public sealed class BuildService(
    WorkspaceLayout layout,
    SteamCmdInstallation installation)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBuilding { get; private set; }

    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Üretilen script'leri workspace'e yazar ve yazılan app script'inin yolunu döndürür.
    /// Build almadan da çağrılabilir (önizleme/hata ayıklama için).
    /// </summary>
    public async Task<string> WriteScriptsAsync(BuildRequest request, CancellationToken ct = default)
    {
        var (profile, app, subApp) = (request.Profile, request.App, request.SubApp);

        var scriptsDir = layout.ScriptsDirectory(profile.Id, app.Id, subApp.Id);
        var outputDir = layout.OutputDirectory(profile.Id, app.Id, subApp.Id);

        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(outputDir);

        var depotScriptPaths = new Dictionary<uint, string>();
        var depots = request.SelectedDepots;

        foreach (var depot in depots)
        {
            var path = layout.DepotScriptPath(profile.Id, app.Id, subApp.Id, depot.DepotId);
            depotScriptPaths[depot.DepotId] = path;

            await VdfWriter.WriteFileAsync(
                path, BuildScriptBuilder.BuildDepotScript(depot, subApp), ct);
        }

        var appScript = BuildScriptBuilder.BuildAppScript(
            subApp, outputDir, depotScriptPaths, request.Description, request.PreviewOverride,
            [.. depots.Select(d => d.DepotId)]);

        var appScriptPath = layout.AppScriptPath(profile.Id, app.Id, subApp);
        await VdfWriter.WriteFileAsync(appScriptPath, appScript, ct);

        return appScriptPath;
    }

    /// <summary>
    /// Doğrulama → oturum kontrolü → script yazımı → SteamCMD sırasıyla ilerler.
    /// Herhangi bir adım geçilemezse <see cref="BuildOutcomeResult.BlockedReason"/> dolar
    /// ve SteamCMD hiç başlatılmaz.
    /// </summary>
    public async Task<BuildOutcomeResult> BuildAsync(
        BuildRequest request,
        IProgress<SteamCmdEvent>? progress = null,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return Blocked(request, Loc.T("Build.AlreadyRunning"));
        }

        IsBuilding = true;

        try
        {
            var depots = request.SelectedDepots;

            if (request.DepotIds is not null && depots.Count == 0)
            {
                return Blocked(request, Loc.T("Build.NoDepotSelected"));
            }

            // Yalnızca gönderilecek depolar denetleniyor: seçilmeyen bir deponun klasörü
            // boş diye diğerlerinin yüklenmesi engellenmesin.
            var validation = ProfileValidator.ValidateSubApp(request.SubApp, depots: depots);

            if (!validation.CanBuild)
            {
                return Blocked(request, string.Join(" ", validation.Issues.Select(i => i.Message)));
            }

            var session = new SteamCmdSessionService(installation);
            var check = await session.CheckAsync(request.Profile.SteamUsername, ct);

            if (!check.CanBuild)
            {
                return Blocked(request, Loc.T("Build.SessionCheckFailed", check.Detail));
            }

            var scriptPath = await WriteScriptsAsync(request, ct);

            var record = new BuildRecord
            {
                SubAppId = request.SubApp.Id,
                SteamAppId = request.SubApp.SteamAppId,
                StartedAt = DateTimeOffset.Now,
                WasPreview = request.SubApp.Preview || request.PreviewOverride,
                Description = request.Description,
                SetLiveBranch = request.SubApp.SetLiveBranch,
                DepotIds = request.IsPartial ? [.. depots.Select(d => d.DepotId)] : null,
            };

            var runner = new SteamCmdRunner(installation) { StallTimeout = StallTimeout };

            var result = await runner.RunAsync(
                $"+login {request.Profile.SteamUsername} +run_app_build \"{scriptPath}\" +quit",
                progress,
                ct);

            Finish(record, result);

            var logPath = Path.Combine(
                layout.OutputDirectory(request.Profile.Id, request.App.Id, request.SubApp.Id),
                $"spm_{record.StartedAt:yyyyMMdd_HHmmss}.log");

            await File.WriteAllTextAsync(logPath, result.RawOutput, ct);
            record.LogFilePath = logPath;

            return new BuildOutcomeResult(record, result.Events);
        }
        finally
        {
            IsBuilding = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Sonuç iki kaynaktan birlikte belirlenir: SteamCMD'nin exit code'u tek başına
    /// güvenilir değil, "Successfully finished" satırı da aranır.
    /// </summary>
    private static void Finish(BuildRecord record, SteamCmdRunResult result)
    {
        record.FinishedAt = DateTimeOffset.Now;
        record.ExitCode = result.ExitCode;

        if (result.Cancelled)
        {
            record.Outcome = BuildOutcome.Cancelled;
            record.FailureReason = Loc.T("Build.CancelledByUser");
            return;
        }

        if (result.SuccessEvent is { } success)
        {
            record.Outcome = BuildOutcome.Succeeded;
            record.SteamBuildId = success.BuildId;
            return;
        }

        record.Outcome = BuildOutcome.Failed;

        record.FailureReason = result switch
        {
            { SawInteractionPrompt: true } => Loc.T("Build.NeedsLogin"),
            { TimedOut: true } => Loc.T("Build.Stalled"),
            _ when result.Events.LastOrDefault(e => e.IsFailure) is { } failure => failure.Message,
            _ => Loc.T("Build.NotFinished", result.ExitCode),
        };
    }

    private static BuildOutcomeResult Blocked(BuildRequest request, string reason) =>
        new(
            new BuildRecord
            {
                SubAppId = request.SubApp.Id,
                SteamAppId = request.SubApp.SteamAppId,
                StartedAt = DateTimeOffset.Now,
                FinishedAt = DateTimeOffset.Now,
                Outcome = BuildOutcome.Failed,
                Description = request.Description,
                FailureReason = reason,
            },
            [],
            reason);
}
