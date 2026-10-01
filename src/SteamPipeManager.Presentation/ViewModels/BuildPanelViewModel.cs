using System.Collections.ObjectModel;
// WPF'in örtük using'leri System.IO'yu getirmiyor; takma adla açıkça belirtiliyor.
using IOFile = System.IO.File;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>Build'in hangi aşamada olduğunu özetler; ham log yerine bu gösterilir.</summary>
/// <summary>Build özetindeki tek satır (etiket + değer).</summary>
public sealed record SummaryRow(string Label, string Value);

/// <summary>Log panelinde gösterilen tek satır.</summary>
public sealed class LogLine(SteamCmdEvent evt)
{
    public string Text => evt.Message;

    public string Time => evt.Timestamp?.ToString("HH:mm:ss") ?? "";

    public StatusTone Tone => evt.Kind switch
    {
        SteamCmdEventKind.BuildSucceeded => StatusTone.Success,
        SteamCmdEventKind.LoginSucceeded => StatusTone.Success,
        SteamCmdEventKind.Error or SteamCmdEventKind.LoginFailed => StatusTone.Danger,
        SteamCmdEventKind.NeedsInteraction => StatusTone.Warning,
        SteamCmdEventKind.UploadingContent or SteamCmdEventKind.ScanningContent
            or SteamCmdEventKind.BuildStarted => StatusTone.Info,
        _ => StatusTone.Neutral,
    };
}

/// <summary>
/// Build çalıştırma, canlı log ve oturum durumu. Seçili build hedefi
/// <see cref="SubAppWorkspaceViewModel"/> tarafından beslenir.
/// </summary>
public sealed partial class BuildPanelViewModel(
    BuildCoordinator coordinator,
    NavigationState navigation,
    IConfirmationService confirmation,
    BuildHistoryStore historyStore) : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<LogLine> Log { get; } = [];

    public ObservableCollection<BuildRecord> History { get; } = [];

    /// <summary>Butona basmadan önce ne yapılacağını gösteren özet.</summary>
    public ObservableCollection<SummaryRow> TargetSummary { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoesLive))]
    [NotifyPropertyChangedFor(nameof(GoesLiveText))]
    private string _liveBranch = "";

    /// <summary>Build canlıya alınacaksa arayüzde ayrıca uyarılır.</summary>
    public bool GoesLive => LiveBranch.Length > 0;

    /// <summary>
    /// Uyarı tek parça metin: cümle yapısı dile göre değiştiği için parçalı
    /// <c>Run</c> öğeleri çeviriyi kırardı.
    /// </summary>
    public string GoesLiveText => AppLocalizer.Instance.Format("Build.GoesLive", LiveBranch);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double? _progressPercent;

    /// <summary>
    /// Hazır metin: nullable double üzerinde XAML <c>StringFormat</c> uygulanmıyor
    /// (aynı tuzağa <c>Duration</c> alanında da düşülmüştü).
    /// </summary>
    public string ProgressText => ProgressPercent is { } pct ? $"{pct:0}%" : "";

    /// <summary>Ham log varsayılan olarak gizli; istenirse butonla açılır.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogToggleText))]
    private bool _isLogVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private PublishPhase _phase = PublishPhase.Idle;

    public string LogToggleText => AppLocalizer.Instance.Get(
        IsLogVisible ? "Build.ToggleLog.Hide" : "Build.ToggleLog.Show");

    public string PhaseText => AppLocalizer.Instance.Get(Phase switch
    {
        PublishPhase.LoggingIn => "Phase.LoggingIn",
        PublishPhase.Preparing => "Phase.Preparing",
        PublishPhase.Scanning => "Phase.Scanning",
        PublishPhase.Uploading => "Phase.Uploading",
        PublishPhase.Succeeded => "Phase.Succeeded",
        PublishPhase.Failed => "Phase.Failed",
        _ => "Phase.Idle",
    });

    public bool IsFinished => Phase.IsFinished();

    public bool HasFailed => Phase == PublishPhase.Failed;

    [ObservableProperty]
    private string? _lastBuildSummary;

    /// <summary>Doğrulama sonucundan gelir; sorunluysa build butonu kapalı kalır.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild))]
    private bool _targetIsValid;

    public bool CanBuild => !IsBusy && TargetIsValid && navigation.SubApp is not null;

    public bool CanCancel => IsBusy;

    [RelayCommand]
    private Task BuildAsync() => RunBuildAsync(preview: false);

    [RelayCommand]
    private Task PreviewAsync() => RunBuildAsync(preview: true);

    [RelayCommand]
    private void Cancel()
    {
        StatusMessage = "İptal ediliyor…";
        _cancellation?.Cancel();
    }

    [RelayCommand]
    private void ToggleLog() => IsLogVisible = !IsLogVisible;

    private async Task RunBuildAsync(bool preview)
    {
        if (navigation.Profile is not { } profile ||
            navigation.App is not { } app ||
            navigation.SubApp is not { } subApp)
        {
            return;
        }

        // Canlıya alma geri alınamaz bir yayın işlemi; önce açıkça onaylatılır.
        if (!preview && subApp.SetLiveBranch is { Length: > 0 } branch)
        {
            var confirmed = await confirmation.ConfirmAsync(
                AppLocalizer.Instance.Get("Build.Confirm.Title"),
                AppLocalizer.Instance.Format("Build.Confirm.Body", branch, app.Title, subApp.Title, subApp.SteamAppId),
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
        Phase = PublishPhase.LoggingIn;
        IsBusy = true;

        _cancellation = new CancellationTokenSource();

        try
        {
            var description = BuildDescription.Render(
                subApp.BuildDescriptionTemplate, app, subApp, profile);

            var request = new BuildRequest(profile, app, subApp, description, preview);

            var progress = new Progress<SteamCmdEvent>(evt =>
            {
                Log.Add(new LogLine(evt));

                if (evt.Percent is { } pct)
                {
                    ProgressPercent = pct;
                }

                Phase = Advance(Phase, evt);

                if (evt.Kind is not (SteamCmdEventKind.Info or SteamCmdEventKind.DepotProgress))
                {
                    StatusMessage = evt.Message;
                }
            });

            var result = await coordinator.BuildAsync(
                request, progress, StatusProgress(), _cancellation.Token);

            if (result.Started)
            {
                await historyStore.AppendAsync(result.Record);
            }

            Summarize(result, preview);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastBuildSummary = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// SteamCMD olayını aşamaya çevirir. Geri düşmeyi engelleyen kural
    /// <see cref="PublishPhases.Advance"/> içinde ve sağlayıcıdan bağımsız;
    /// buradaki eşleme yalnızca Steam'e özgü olan kısım.
    /// </summary>
    private static PublishPhase Advance(PublishPhase current, SteamCmdEvent evt)
    {
        var candidate = evt.Kind switch
        {
            SteamCmdEventKind.LoginStarted => PublishPhase.LoggingIn,
            SteamCmdEventKind.LoginSucceeded => PublishPhase.Preparing,
            SteamCmdEventKind.BuildStarted => PublishPhase.Preparing,
            SteamCmdEventKind.ScanningContent => PublishPhase.Scanning,
            SteamCmdEventKind.DepotProgress when evt.Percent is not null => PublishPhase.Scanning,
            SteamCmdEventKind.UploadingContent => PublishPhase.Uploading,
            SteamCmdEventKind.BuildSucceeded => PublishPhase.Succeeded,
            _ => current,
        };

        return PublishPhases.Advance(current, candidate);
    }

    private void Summarize(BuildOutcomeResult result, bool preview)
    {
        if (!result.Started)
        {
            // Sebep en önemli bilgi; durum satırına da yazılır ki kullanıcı
            // yalnızca "başlatılmadı" görmesin.
            LastBuildSummary = result.BlockedReason;
            StatusMessage = result.BlockedReason ?? AppLocalizer.Instance.Get("Build.NotStarted");
            Phase = PublishPhase.Failed;
            IsLogVisible = true;

            return;
        }

        var record = result.Record;

        Phase = record.Outcome == BuildOutcome.Succeeded ? PublishPhase.Succeeded : PublishPhase.Failed;

        // Hata durumunda log otomatik açılır — asıl ihtiyaç duyulduğu an orası.
        if (record.Outcome != BuildOutcome.Succeeded)
        {
            IsLogVisible = true;
        }

        LastBuildSummary = record.Outcome switch
        {
            BuildOutcome.Succeeded when preview => AppLocalizer.Instance.Get("Build.PreviewDone"),
            BuildOutcome.Succeeded =>
                AppLocalizer.Instance.Format("Build.Succeeded", record.SteamBuildId?.ToString() ?? "—", record.DurationText),
            BuildOutcome.Cancelled => AppLocalizer.Instance.Get("Build.Cancelled"),
            _ => record.FailureReason ?? AppLocalizer.Instance.Get("Build.Failed"),
        };

        StatusMessage = LastBuildSummary;
    }

    private Progress<string> StatusProgress() => new(text => StatusMessage = text);

    /// <summary>Seçili hedef değiştiğinde panel sıfırlanır ve o hedefin geçmişi yüklenir.</summary>
    public void OnTargetChanged(bool isValid)
    {
        TargetIsValid = isValid;

        if (!IsBusy)
        {
            Log.Clear();
            ProgressPercent = null;
            LastBuildSummary = null;
            Phase = PublishPhase.Idle;
            StatusMessage = isValid ? "" : AppLocalizer.Instance.Get("Build.TargetInvalid");
        }

        BuildSummary();
        _ = LoadHistoryAsync();
    }

    private void BuildSummary()
    {
        TargetSummary.Clear();
        LiveBranch = "";

        if (navigation.Profile is not { } profile ||
            navigation.App is not { } app ||
            navigation.SubApp is not { } subApp)
        {
            return;
        }

        LiveBranch = subApp.SetLiveBranch ?? "";

        TargetSummary.Add(new(AppLocalizer.Instance.Get("Build.Summary.Target"), $"{app.Title} — {subApp.Title}"));
        TargetSummary.Add(new(AppLocalizer.Instance.Get("Build.Summary.AppId"), subApp.SteamAppId.ToString()));
        TargetSummary.Add(new(AppLocalizer.Instance.Get("Build.Summary.Account"), profile.SteamUsername));

        TargetSummary.Add(new(
            AppLocalizer.Instance.Get(subApp.Depots.Count == 1 ? "Build.Summary.Depot" : "Build.Summary.Depots"),
            string.Join(", ", subApp.Depots.Select(d =>
                d.Label is { Length: > 0 } label ? $"{d.DepotId} ({label})" : d.DepotId.ToString()))));

        TargetSummary.Add(new(
            AppLocalizer.Instance.Get("Build.Summary.Branch"),
            LiveBranch.Length > 0 ? LiveBranch : AppLocalizer.Instance.Get("Build.Summary.NoBranch")));

        TargetSummary.Add(new(
            AppLocalizer.Instance.Get("Build.Summary.Description"),
            BuildDescription.Render(subApp.BuildDescriptionTemplate, app, subApp, profile)));
    }

    private async Task LoadHistoryAsync()
    {
        History.Clear();

        if (navigation.SubApp is not { } subApp)
        {
            return;
        }

        try
        {
            foreach (var record in await historyStore.ForSubAppAsync(subApp.Id))
            {
                History.Add(record);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Arşivlenen ham SteamCMD çıktısını Gezgin'de gösterir.</summary>
    [RelayCommand]
    private void OpenLogFolder(BuildRecord record)
    {
        if (record.LogFilePath is not { Length: > 0 } path || !IOFile.Exists(path))
        {
            StatusMessage = AppLocalizer.Instance.Get("History.NoLog");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }
}
