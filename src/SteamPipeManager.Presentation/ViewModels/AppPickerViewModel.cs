using System.Collections.ObjectModel;
// WPF'in örtük using'leri System.Windows.Shapes.Path'i getirdiği için takma ad kullanılıyor.
using IOPath = System.IO.Path;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Vdf;

namespace SteamPipeManager.Presentation.ViewModels;

public sealed partial class AppCard(SteamApp app) : ObservableObject
{
    public SteamApp App { get; } = app;

    public string Title => App.Title;

    public string Summary => App.SubApps.Count == 0
        ? AppLocalizer.Instance.Get("Apps.NoTargets")
        : string.Join(" · ", App.SubApps.Select(s => s.Title));

    public string AppIds => string.Join(", ", App.SubApps.Select(s => s.SteamAppId));

    /// <summary>
    /// Steam dikey kütüphane kapsülü (600x900). Henüz yayınlanmamış app'lerde görsel
    /// yok (404); o durumda yer tutucu gösterilir.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapsule))]
    private string? _capsulePath;

    public bool HasCapsule => CapsulePath is { Length: > 0 };

    /// <summary>Kapsül yoksa yer tutucudaki baş harf.</summary>
    public string Initial => App.Title is { Length: > 0 } title
        ? title[..1].ToUpperInvariant()
        : "?";

    /// <summary>Kapsül görselinin çekileceği AppID: ana oyun varsa o, yoksa ilk hedef.</summary>
    public uint? CapsuleAppId =>
        App.SubApps.FirstOrDefault(s => s.Kind == SubAppKind.Main)?.SteamAppId
        ?? App.SubApps.FirstOrDefault()?.SteamAppId;
}

public sealed partial class AppPickerViewModel(
    ProfileRepository repository,
    NavigationState navigation,
    IDialogService dialogs,
    SteamImageService images,
    IConfirmationService confirmation) : ObservableObject
{
    public ObservableCollection<AppCard> Cards { get; } = [];

    public bool HasApps => Cards.Count > 0;

    /// <summary>
    /// ContentBuilder içe aktarma Steam'e özel: Epic'te script dosyası kavramı yok,
    /// dolayısıyla o düğme Epic profillerinde gösterilmiyor.
    /// </summary>
    public bool IsSteamProfile =>
        navigation.Profile?.Provider != PublishProviderId.Epic;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importPath = "";

    public ObservableCollection<ImportCandidate> ImportCandidates { get; } = [];

    /// <summary>Adı düzenlenen oyun; null ise düzenleme kapalı.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private AppCard? _editing;

    [ObservableProperty]
    private string _editTitle = "";

    public bool IsEditing => Editing is not null;

    public void Refresh()
    {
        Cards.Clear();

        foreach (var app in navigation.Profile?.Apps ?? [])
        {
            Cards.Add(new AppCard(app));
        }

        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(IsSteamProfile));

        _ = LoadCapsulesAsync();
    }

    /// <summary>
    /// Kapsül görsellerini arka planda çeker; önbellekte varsa anında gelir.
    /// Bulunamayanlar yer tutucuda kalır ve gün içinde tekrar denenmez.
    /// </summary>
    private async Task LoadCapsulesAsync()
    {
        foreach (var card in Cards.ToList())
        {
            if (card.CapsuleAppId is not { } appId)
            {
                continue;
            }

            try
            {
                card.CapsulePath = await images.GetAppLibraryCapsuleAsync(appId);
            }
            catch
            {
                // Görsel olmadan da çalışmaya devam edilir.
            }
        }
    }

    [RelayCommand]
    private void Select(AppCard card) => navigation.SelectApp(card.App);


    [RelayCommand]
    private async Task AddAppAsync()
    {
        if (navigation.Profile is not { } profile)
        {
            return;
        }

        var app = new SteamApp { Title = AppLocalizer.Instance.Get("Apps.NewGame") };

        // Epic profilindeki bir oyunun Epic ayarları olmalı; artifact listesi orada
        // yaşıyor ve çalışma alanı onu bekliyor.
        if (profile.Provider == PublishProviderId.Epic)
        {
            app.Epic = new EpicGameSettings();
        }

        profile.Apps.Add(app);
        await repository.SaveAsync();

        Refresh();
        navigation.SelectApp(app);
    }

    // --- Oyun düzenleme ve silme ---

    [RelayCommand]
    private void BeginEdit(AppCard card)
    {
        Editing = card;
        EditTitle = card.Title;
        StatusMessage = null;
    }

    [RelayCommand]
    private void CancelEdit() => Editing = null;

    [RelayCommand]
    private async Task ConfirmEditAsync()
    {
        if (Editing is not { } card)
        {
            return;
        }

        var title = EditTitle.Trim();

        if (title.Length == 0)
        {
            StatusMessage = AppLocalizer.Instance.Get("Validate.GameNameRequired");
            return;
        }

        card.App.Title = title;
        await repository.SaveAsync();

        Editing = null;
        Refresh();
    }

    /// <summary>
    /// Oyunu ve altındaki tüm build hedeflerini siler. Geri alınamaz, bu yüzden
    /// kaç hedefin gideceği söylenerek onay isteniyor.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync(AppCard card)
    {
        if (navigation.Profile is not { } profile)
        {
            return;
        }

        var confirmed = await confirmation.ConfirmAsync(
            AppLocalizer.Instance.Get("Apps.Delete.Title"),
            AppLocalizer.Instance.Format("Apps.Delete.Body", card.Title, card.App.SubApps.Count),
            confirmText: AppLocalizer.Instance.Get("Common.Delete"),
            isDestructive: true);

        if (!confirmed)
        {
            return;
        }

        profile.Apps.Remove(card.App);
        await repository.SaveAsync();

        Refresh();
        StatusMessage = AppLocalizer.Instance.Format("Apps.Deleted", card.Title);
    }

    // --- İçe aktarma ---

    [RelayCommand]
    private void BeginImport()
    {
        ImportCandidates.Clear();
        StatusMessage = null;
        IsImporting = true;
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsImporting = false;
        ImportCandidates.Clear();
    }

    [RelayCommand]
    private async Task BrowseImportPathAsync()
    {
        if (await dialogs.PickFolderAsync(AppLocalizer.Instance.Get("Import.Title"), ImportPath) is { } picked)
        {
            ImportPath = picked;
            ScanImportPath();
        }
    }

    /// <summary>Seçilen ContentBuilder klasörünü tarar ve onaya sunulacak listeyi doldurur.</summary>
    [RelayCommand]
    private void ScanImportPath()
    {
        ImportCandidates.Clear();

        var result = ContentBuilderImporter.ImportFrom(ImportPath);

        foreach (var imported in result.SubApps)
        {
            ImportCandidates.Add(new ImportCandidate(imported));
        }

        StatusMessage = result.SubApps.Count switch
        {
            0 => string.Join(" ", result.Warnings.DefaultIfEmpty(AppLocalizer.Instance.Get("Import.NothingFound"))),
            1 => AppLocalizer.Instance.Get("Import.FoundOne"),
            var n => AppLocalizer.Instance.Format("Import.Found", n),
        };
    }

    /// <summary>
    /// Onaylanan hedefleri profile ekler. Aynı içerik klasörünü paylaşan hedefler
    /// (ör. ana oyun + beta) tek bir oyun altında toplanır.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        if (navigation.Profile is not { } profile)
        {
            return;
        }

        var selected = ImportCandidates.Where(c => c.IsSelected).ToList();

        if (selected.Count == 0)
        {
            StatusMessage = AppLocalizer.Instance.Get("Import.NoneSelected");
            return;
        }

        foreach (var group in selected.GroupBy(c => c.GroupKey))
        {
            var app = new SteamApp { Title = group.First().SuggestedAppTitle };

            foreach (var candidate in group)
            {
                app.SubApps.Add(candidate.SubApp);
            }

            profile.Apps.Add(app);
        }

        await repository.SaveAsync();

        IsImporting = false;
        ImportCandidates.Clear();
        Refresh();
        StatusMessage = AppLocalizer.Instance.Format("Import.Done", selected.Count);
    }
}

/// <summary>İçe aktarma onay listesindeki tek satır.</summary>
public sealed partial class ImportCandidate : ObservableObject
{
    public ImportCandidate(ImportedSubApp imported)
    {
        SubApp = imported.SubApp;
        SourceFile = IOPath.GetFileName(imported.SourceAppScript);
        Warnings = imported.Warnings;

        // Aynı içerik klasörünü kullanan hedefler aynı oyunun parçası kabul edilir
        // (Pixel Racer'ın ana app'i ile beta app'i bu durumda).
        GroupKey = SubApp.Depots
            .Select(d => d.ContentRoot)
            .FirstOrDefault(r => r is { Length: > 0 }) ?? SubApp.SteamAppId.ToString();

        SuggestedAppTitle = ContentBuilderImporter.SuggestAppTitle(imported);

        // SDK örnekleri her kurulumda bulunur ve hepsi AppID 1000 kullanır;
        // varsayılan olarak seçili gelmezler.
        IsSdkSample = imported.IsSdkSample;
        IsSelected = !imported.IsSdkSample;
    }

    public bool IsSdkSample { get; }

    public SubApp SubApp { get; }

    public string SourceFile { get; }

    public IReadOnlyList<string> Warnings { get; }

    public string GroupKey { get; }

    public string SuggestedAppTitle { get; }

    public string Description =>
        $"AppID {SubApp.SteamAppId} · {SubApp.Depots.Count} depot · {SubApp.Kind}";

    public string WarningText => Warnings.Count == 0 ? "" : string.Join(" ", Warnings);

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    private bool _isSelected = true;
}
