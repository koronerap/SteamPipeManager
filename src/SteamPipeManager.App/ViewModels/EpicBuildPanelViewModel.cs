using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.App.ViewModels;

/// <summary>Log panelindeki tek satır.</summary>
public sealed class EpicLogLine(BptEvent evt)
{
    public string Text => evt.Message;

    public string Time => evt.Timestamp?.ToString("HH:mm:ss") ?? "";

    public Brush Color => evt.Kind switch
    {
        BptEventKind.Succeeded or BptEventKind.DryRunPassed => Brushes.MediumSeaGreen,
        BptEventKind.AuthenticationFailed or BptEventKind.ValidationFailed
            or BptEventKind.Failed or BptEventKind.DryRunFailed => Brushes.IndianRed,
        BptEventKind.Started or BptEventKind.ScanProgress
            or BptEventKind.UploadProgress => Brushes.CornflowerBlue,
        _ => Brushes.Gray,
    };
}

/// <summary>
/// Epic build paneli.
///
/// Steam panelinden ayrı, çünkü olay tipi ve akış farklı — özellikle canlıya almanın
/// ayrı bir adım olması. Faz modeli, ilerleme ve geçmiş ise ortak
/// (<see cref="PublishPhase"/>, <see cref="BuildRecord"/>).
/// </summary>
public sealed partial class EpicBuildPanelViewModel(
    NavigationState navigation,
    ISettingsStore settingsStore,
    WorkspaceLayout layout,
    IConfirmationService confirmation,
    EpicSecretStore secrets,
    BuildHistoryStore historyStore,
    IDialogService dialogs) : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<EpicLogLine> Log { get; } = [];

    public ObservableCollection<BuildRecord> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private PublishPhase _phase = PublishPhase.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double? _progressPercent;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _lastBuildSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogToggleText))]
    private bool _isLogVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild))]
    private bool _targetIsValid;

    /// <summary>Kısmi başarısızlık: yüklendi ama canlıya alınamadı.</summary>
    [ObservableProperty]
    private bool _uploadedButNotLabelled;

    public string ProgressText => ProgressPercent is { } pct ? $"{pct:0}%" : "";

    public string LogToggleText => AppLocalizer.Instance.Get(
        IsLogVisible ? "Build.ToggleLog.Hide" : "Build.ToggleLog.Show");

    public string PhaseText => AppLocalizer.Instance.Get(Phase switch
    {
        PublishPhase.LoggingIn => "Phase.LoggingIn",
        PublishPhase.Preparing => "Phase.Preparing",
        PublishPhase.Scanning => "Epic.Phase.Chunking",
        PublishPhase.Uploading => "Phase.Uploading",
        PublishPhase.Succeeded => "Phase.Succeeded",
        PublishPhase.Failed => "Phase.Failed",
        _ => "Phase.Idle",
    });

    public bool IsFinished => Phase.IsFinished();

    public bool HasFailed => Phase == PublishPhase.Failed;

    public bool CanBuild => !IsBusy && TargetIsValid && Artifact is not null;

    public bool CanCancel => IsBusy;

    private EpicArtifact? Artifact { get; set; }

    /// <summary>Seçili hedef değiştiğinde çağrılır.</summary>
    public void OnTargetChanged(EpicArtifact? artifact, bool isValid)
    {
        Artifact = artifact;
        TargetIsValid = isValid;

        Phase = PublishPhase.Idle;
        ProgressPercent = null;
        LastBuildSummary = null;
        UploadedButNotLabelled = false;
        Log.Clear();

        OnPropertyChanged(nameof(CanBuild));
        _ = LoadHistoryAsync();
    }

    [RelayCommand]
    private Task BuildAsync() => RunAsync(preview: false);

    [RelayCommand]
    private Task PreviewAsync() => RunAsync(preview: true);

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void ToggleLog() => IsLogVisible = !IsLogVisible;

    /// <summary>
    /// Build'in log dosyasını maskeleyip paylaşılabilir bir dosyaya yazar.
    ///
    /// Epic desteği gerçek bir hesaba karşı doğrulanana kadar deneysel; o doğrulamayı
    /// yapacak kişinin log'u kolayca gönderebilmesi gerekiyor. Kimlikler ve yollar
    /// maskeleniyor, teknik satırlar olduğu gibi kalıyor.
    /// </summary>
    [RelayCommand]
    private async Task ExportLogAsync(BuildRecord? record)
    {
        var source = (record ?? History.FirstOrDefault())?.LogFilePath;

        if (source is not { Length: > 0 } || !File.Exists(source))
        {
            StatusMessage = AppLocalizer.Instance.Get("History.NoLog");
            return;
        }

        var version = (record ?? History.FirstOrDefault())?.EpicBuildVersion ?? "build";
        var suggested = $"bpt-{EpicBuildVersion.Normalize(version)}.log";

        if (dialogs.PickSaveFile(
                AppLocalizer.Instance.Get("Epic.ExportLog"),
                suggested,
                "Log (*.log)|*.log|Metin (*.txt)|*.txt") is not { } target)
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(source);
            var when = (record ?? History.FirstOrDefault())?.StartedAt ?? DateTimeOffset.Now;

            await File.WriteAllTextAsync(
                target, BptLogRedactor.Header(version, when) + BptLogRedactor.Redact(text));

            StatusMessage = AppLocalizer.Instance.Format("Epic.ExportLog.Done", target);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RunAsync(bool preview)
    {
        if (navigation.Profile is not { Epic: { } epicProfile } profile ||
            navigation.App is not { Epic: { } game } app ||
            Artifact is not { } artifact)
        {
            return;
        }

        var settings = await settingsStore.LoadAsync();

        if (BptInstallation.LocateExecutable(settings.BuildPatchToolPath ?? "") is not { } exe)
        {
            StatusMessage = AppLocalizer.Instance.Get("Epic.ToolPathMissing");
            LastBuildSummary = StatusMessage;
            Phase = PublishPhase.Failed;

            return;
        }

        if (secrets.Read(profile.Id) is not { Length: > 0 } secret)
        {
            StatusMessage = AppLocalizer.Instance.Get("Epic.SecretMissing");
            LastBuildSummary = StatusMessage;
            Phase = PublishPhase.Failed;

            return;
        }

        // Canlıya alma geri alınamaz bir yayın işlemi; önce açıkça onaylatılır.
        if (!preview && !artifact.Preview && artifact.Label is { Length: > 0 } label)
        {
            var confirmed = await confirmation.ConfirmAsync(
                AppLocalizer.Instance.Get("Build.Confirm.Title"),
                AppLocalizer.Instance.Format(
                    "Epic.Confirm.Body", label, app.Title, artifact.Title, artifact.ArtifactId),
                confirmText: AppLocalizer.Instance.Get("Build.Confirm.Ok"),
                isDestructive: true);

            if (!confirmed)
            {
                StatusMessage = AppLocalizer.Instance.Get("Build.Cancelled");
                return;
            }
        }

        Log.Clear();
        ProgressPercent = null;
        LastBuildSummary = null;
        UploadedButNotLabelled = false;
        Phase = PublishPhase.Preparing;
        IsBusy = true;

        _cancellation = new CancellationTokenSource();

        try
        {
            var service = new EpicBuildService(
                new BptInstallation(exe), Path.Combine(layout.LogsDirectory, "epic"))
            {
                StallTimeout = TimeSpan.FromSeconds(settings.StallWarningSeconds),
                KnownVersions = _ => KnownVersions(artifact),
            };

            var request = new EpicBuildRequest(
                profile, app, artifact,
                new EpicCredentials(
                    epicProfile.OrganizationId, game.ProductId, artifact.ArtifactId,
                    epicProfile.ClientId, secret))
            {
                PreviewOverride = preview,
            };

            var progress = new Progress<BptEvent>(evt =>
            {
                Log.Add(new EpicLogLine(evt));

                if (evt.Percent is { } pct)
                {
                    ProgressPercent = pct;
                }

                Phase = PublishPhases.Advance(Phase, PhaseOf(evt));

                if (evt.Kind is not (BptEventKind.Info or BptEventKind.ScanProgress
                    or BptEventKind.Configuration))
                {
                    StatusMessage = evt.Message;
                }
            });

            var outcome = await service.BuildAsync(request, progress, _cancellation.Token);

            if (outcome.Started)
            {
                await historyStore.AppendAsync(outcome.Record);
            }

            Summarize(outcome, preview);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastBuildSummary = ex.Message;
            Phase = PublishPhase.Failed;
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// BPT olayını aşamaya çevirir. Geri düşmeyi engelleyen kural
    /// <see cref="PublishPhases.Advance"/> içinde ve sağlayıcıdan bağımsız.
    ///
    /// Not: yükleme aşamasının satırları henüz gerçek bir hesapla ölçülemedi
    /// (bkz. docs/E0-BPT-FINDINGS.md), bu yüzden eşleme bilerek toleranslı —
    /// tanınmayan satır aşamayı değiştirmiyor, düşürülmüyor de.
    /// </summary>
    private static PublishPhase PhaseOf(BptEvent evt) => evt.Kind switch
    {
        BptEventKind.Configuration => PublishPhase.Preparing,
        BptEventKind.Started => PublishPhase.Preparing,
        BptEventKind.FilesEnumerated => PublishPhase.Scanning,
        BptEventKind.ScanProgress => PublishPhase.Scanning,
        BptEventKind.UploadProgress => PublishPhase.Uploading,
        BptEventKind.ManifestSaved => PublishPhase.Uploading,
        BptEventKind.Succeeded or BptEventKind.DryRunPassed => PublishPhase.Succeeded,
        BptEventKind.Failed or BptEventKind.AuthenticationFailed
            or BptEventKind.ValidationFailed or BptEventKind.DryRunFailed => PublishPhase.Failed,
        _ => PublishPhase.Idle,
    };

    private void Summarize(EpicBuildOutcome outcome, bool preview)
    {
        if (!outcome.Started)
        {
            LastBuildSummary = outcome.BlockedReason;
            StatusMessage = outcome.BlockedReason ?? AppLocalizer.Instance.Get("Build.NotStarted");
            Phase = PublishPhase.Failed;
            IsLogVisible = true;

            return;
        }

        var record = outcome.Record;

        UploadedButNotLabelled = outcome.UploadedButNotLabelled;
        Phase = record.Outcome == BuildOutcome.Succeeded ? PublishPhase.Succeeded : PublishPhase.Failed;

        LastBuildSummary = record.Outcome switch
        {
            BuildOutcome.Succeeded when preview || record.WasPreview =>
                AppLocalizer.Instance.Get("Epic.PreviewDone"),

            BuildOutcome.Succeeded =>
                AppLocalizer.Instance.Format(
                    "Epic.Succeeded", record.EpicBuildVersion ?? "", record.DurationText),

            BuildOutcome.Cancelled => AppLocalizer.Instance.Get("Build.Cancelled"),
            _ => record.FailureReason ?? AppLocalizer.Instance.Get("Build.Failed"),
        };

        // Başarısızlıkta log kendiliğinden açılıyor: sebebi aramak için düğmeye
        // basmak zorunda kalmak, hata anında istenen son şey.
        if (record.Outcome != BuildOutcome.Succeeded)
        {
            IsLogVisible = true;
        }
    }

    /// <summary>
    /// Bu hedefte daha önce kullanılmış sürümler. Epic'te sürüm tekrarı çakışma
    /// demek, o yüzden yeni sürüm üretilirken bunlardan kaçınılıyor.
    /// </summary>
    private IReadOnlyCollection<string> KnownVersions(EpicArtifact artifact) =>
        [.. History
            .Where(r => r.SubAppId == artifact.Id && r.EpicBuildVersion is { Length: > 0 })
            .Select(r => r.EpicBuildVersion!)];

    private async Task LoadHistoryAsync()
    {
        History.Clear();

        if (Artifact is not { } artifact)
        {
            return;
        }

        foreach (var record in await historyStore.ForSubAppAsync(artifact.Id))
        {
            History.Add(record);
        }
    }
}
