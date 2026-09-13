using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Vdf;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.App.ViewModels;

/// <summary>
/// Sol listedeki tek build hedefi. Her yan uygulamanın Steam'de kendi AppID'si ve
/// dolayısıyla kendi kapsül görseli var.
/// </summary>
public sealed partial class SubAppCard(SubApp subApp) : ObservableObject
{
    public SubApp SubApp { get; } = subApp;

    public string Title => SubApp.Title;

    public string Subtitle => $"{SubApp.Kind} · {SubApp.SteamAppId}";

    public string Initial => SubApp.Title is { Length: > 0 } title
        ? title[..1].ToUpperInvariant()
        : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapsule))]
    private string? _capsulePath;

    public bool HasCapsule => CapsulePath is { Length: > 0 };

    /// <summary>
    /// Model üzerindeki alanlar doğrudan düzenlendiği için (POCO, bildirim yok) soldaki
    /// listenin metinlerini tazelemek gerekiyor.
    /// </summary>
    public void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Initial));
    }
}

/// <summary>
/// Seçili oyunun build hedefleri ve seçili hedefin ayarları.
/// Build motoru M3'te bağlanacak; bu aşamada script önizleme ve doğrulama çalışıyor.
/// </summary>
public sealed partial class SubAppWorkspaceViewModel(
    ProfileRepository repository,
    NavigationState navigation,
    WorkspaceLayout layout,
    BuildPanelViewModel buildPanel,
    IDialogService dialogs,
    SteamImageService images,
    IConfirmationService confirmation) : ObservableObject
{
    public NavigationState Navigation { get; } = navigation;

    public BuildPanelViewModel BuildPanel { get; } = buildPanel;

    public ObservableCollection<SubAppCard> SubApps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(Selected))]
    private SubAppCard? _selectedCard;

    /// <summary>Seçili hedefin modeli; XAML bağlamaları bunun üzerinden çalışıyor.</summary>
    public SubApp? Selected => SelectedCard?.SubApp;

    /// <summary>Sekmelerdeki Build'in sırası; hızlı build oraya geçiyor.</summary>
    public const int BuildTabIndex = 2;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _scriptPreview = "";

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public bool HasSelection => Selected is not null;

    public bool CanBuild => Issues.Count == 0 && Selected is not null;

    public string AppTitle => Navigation.App?.Title ?? "";

    private bool _refreshing;

    private CancellationTokenSource? _autoSave;

    /// <summary>
    /// Otomatik kaydetme gecikmesi. Her tuş vuruşunda diske yazmamak için kısa bir
    /// bekleme var; yazmadan önce sayfadan çıkılırsa <see cref="FlushAsync"/> devreye girer.
    /// </summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Arayüzdeki her düzenleme buradan geçer: doğrulama ve script önizlemesi anında
    /// güncellenir, diske yazma ise kısa bir gecikmeyle yapılır. Kaydet butonları
    /// bunun yerine kaldırıldı.
    /// </summary>
    public void ScheduleSave()
    {
        Validate();
        UpdatePreview();
        SelectedCard?.NotifyModelChanged();

        _autoSave?.Cancel();
        _autoSave?.Dispose();

        var pending = new CancellationTokenSource();
        _autoSave = pending;

        _ = SaveAfterDelayAsync(pending.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, ct);
            await repository.SaveAsync(ct);
            StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            // Ardından gelen düzenleme yazmayı devraldı.
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// Bekleyen otomatik kaydı hemen diske yazar. Hedef değiştirilirken, sayfadan
    /// çıkılırken ve uygulama kapanırken çağrılır; aksi hâlde son 400 ms'lik
    /// düzenleme kaybolabilirdi.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_autoSave is not { } pending)
        {
            return;
        }

        await pending.CancelAsync();
        pending.Dispose();
        _autoSave = null;

        try
        {
            await repository.SaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    public void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;

        try
        {
            SubApps.Clear();

            foreach (var subApp in Navigation.App?.SubApps ?? [])
            {
                SubApps.Add(new SubAppCard(subApp));
            }

            // Önceki seçim hâlâ listedeyse korunur; yoksa ilk hedefe düşülür.
            SelectedCard = SubApps.FirstOrDefault(c => c.SubApp == Navigation.SubApp)
                ?? SubApps.FirstOrDefault();

            OnPropertyChanged(nameof(AppTitle));
            _ = LoadCapsulesAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedCardChanged(SubAppCard? oldValue, SubAppCard? newValue)
    {
        // Önceki hedefin bekleyen düzenlemesi burada yazılır; yoksa hedef değiştirince
        // kaybolurdu.
        _ = FlushAsync();

        Unhook(oldValue?.SubApp);
        Hook(newValue?.SubApp);

        Navigation.SubApp = newValue?.SubApp;
        Validate();
        UpdatePreview();
    }

    /// <summary>
    /// Seçili hedefin ve depot'larının değişimlerini dinlemeye başlar. Arayüz modele
    /// doğrudan yazdığı için otomatik kaydetmenin tetiklendiği yer burası.
    /// </summary>
    private void Hook(SubApp? subApp)
    {
        if (subApp is null)
        {
            return;
        }

        subApp.PropertyChanged += OnModelChanged;
        subApp.Depots.CollectionChanged += OnDepotsChanged;

        foreach (var depot in subApp.Depots)
        {
            depot.PropertyChanged += OnModelChanged;
        }
    }

    private void Unhook(SubApp? subApp)
    {
        if (subApp is null)
        {
            return;
        }

        subApp.PropertyChanged -= OnModelChanged;
        subApp.Depots.CollectionChanged -= OnDepotsChanged;

        foreach (var depot in subApp.Depots)
        {
            depot.PropertyChanged -= OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void OnDepotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleSave();

        foreach (var depot in e.OldItems?.OfType<DepotConfig>() ?? [])
        {
            depot.PropertyChanged -= OnModelChanged;
        }

        foreach (var depot in e.NewItems?.OfType<DepotConfig>() ?? [])
        {
            depot.PropertyChanged += OnModelChanged;
        }
    }

    public void Validate()
    {
        Issues.Clear();

        if (Selected is null)
        {
            OnPropertyChanged(nameof(CanBuild));
            BuildPanel.OnTargetChanged(false);
            return;
        }

        foreach (var issue in ProfileValidator.ValidateSubApp(Selected).Issues)
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(CanBuild));
        BuildPanel.OnTargetChanged(CanBuild);
    }

    /// <summary>
    /// Build almadan üretilecek VDF'i gösterir. Script'ler her build öncesi yeniden
    /// üretildiği için kullanıcının tam olarak ne gönderileceğini görebilmesi gerekir.
    /// </summary>
    public void UpdatePreview()
    {
        if (Selected is not { } subApp || Navigation.Profile is not { } profile || Navigation.App is not { } app)
        {
            ScriptPreview = "";
            return;
        }

        try
        {
            var scriptPaths = subApp.Depots.ToDictionary(
                d => d.DepotId,
                d => layout.DepotScriptPath(profile.Id, app.Id, subApp.Id, d.DepotId));

            var appScript = BuildScriptBuilder.BuildAppScript(
                subApp,
                layout.OutputDirectory(profile.Id, app.Id, subApp.Id),
                scriptPaths,
                BuildDescription.Render(subApp.BuildDescriptionTemplate, app, subApp, profile));

            var sections = new List<string>
            {
                $"=== {WorkspaceLayout.AppScriptFileName(subApp.SteamAppId)} ===",
                VdfWriter.Write(appScript),
            };

            foreach (var depot in subApp.Depots)
            {
                sections.Add($"\r\n=== {WorkspaceLayout.DepotScriptFileName(depot.DepotId)} ===");
                sections.Add(VdfWriter.Write(BuildScriptBuilder.BuildDepotScript(depot, subApp)));
            }

            ScriptPreview = string.Join("\r\n", sections);
        }
        catch (Exception ex)
        {
            ScriptPreview = ex.Message;
        }
    }

    /// <summary>
    /// Her hedefin kapsül görselini çeker. Önbellekte varsa anında gelir; yayınlanmamış
    /// app'lerde görsel olmadığı için yer tutucuda kalınır.
    /// </summary>
    private async Task LoadCapsulesAsync()
    {
        foreach (var card in SubApps.ToList())
        {
            if (card.SubApp.SteamAppId == 0)
            {
                continue;
            }

            try
            {
                card.CapsulePath = await images.GetAppLibraryCapsuleAsync(card.SubApp.SteamAppId);
            }
            catch
            {
                // Görsel olmadan da çalışılır.
            }
        }
    }

    /// <summary>
    /// Hedef satırındaki build düğmesi: hedefi seçer, Build sekmesine geçer ve
    /// build'i başlatır. Sekmeye geçmek şart — kullanıcı ne olduğunu görmeli.
    ///
    /// Canlıya alma onayı atlanmıyor; onu build paneli soruyor.
    /// </summary>
    [RelayCommand]
    private void QuickBuild(SubAppCard? card)
    {
        if (card is not null)
        {
            SelectedCard = card;
        }

        StartQuickBuild();
    }

    public void StartQuickBuild()
    {
        SelectedTabIndex = BuildTabIndex;

        if (BuildPanel.BuildCommand.CanExecute(null))
        {
            BuildPanel.BuildCommand.Execute(null);
            return;
        }

        // Başlayamıyorsa sebebi söylenmeli. Sessizce "Hazır"da durmak, kullanıcıya
        // düğmenin çalışmadığını düşündürür; oysa sorun hedefte.
        BuildPanel.StatusMessage = Issues.Count > 0
            ? AppLocalizer.Instance.Format(
                "Apps.QuickBuild.Blocked", string.Join(" ", Issues.Select(i => i.Message)))
            : AppLocalizer.Instance.Get("Build.NotStarted");
    }

    [RelayCommand]
    private async Task AddSubAppAsync()
    {
        if (Navigation.App is not { } app)
        {
            return;
        }

        var subApp = new SubApp
        {
            Title = AppLocalizer.Instance.Get("Workspace.AddSubApp"),
            Kind = app.SubApps.Any(s => s.Kind == SubAppKind.Main) ? SubAppKind.Demo : SubAppKind.Main,
        };

        app.SubApps.Add(subApp);
        await repository.SaveAsync();

        Refresh();
        SelectedCard = SubApps.FirstOrDefault(c => c.SubApp == subApp);
    }

    /// <summary>
    /// Build hedefini siler. Geri alınamaz olduğu için onay isteniyor; üretilmiş
    /// script'ler ve build geçmişi diskte kalır, yalnızca tanım silinir.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSubAppAsync(SubAppCard? card)
    {
        if ((card ?? SelectedCard) is not { } target || Navigation.App is not { } app)
        {
            return;
        }

        var confirmed = await confirmation.ConfirmAsync(
            AppLocalizer.Instance.Get("Workspace.Delete.Title"),
            AppLocalizer.Instance.Format(
                "Workspace.Delete.Body", target.Title, target.SubApp.SteamAppId),
            confirmText: AppLocalizer.Instance.Get("Common.Delete"),
            isDestructive: true);

        if (!confirmed)
        {
            return;
        }

        app.SubApps.Remove(target.SubApp);
        await repository.SaveAsync();
        Refresh();
    }

    /// <summary>Ctrl+S: kaydetme zaten otomatik, bu yalnızca beklemeyi atlar.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        await FlushAsync();
        Validate();
        UpdatePreview();
        StatusMessage = AppLocalizer.Instance.Get("Settings.Saved");
    }

    [RelayCommand]
    private void AddDepot()
    {
        if (Selected is null)
        {
            return;
        }

        // Koleksiyon değişimi Hook üzerinden otomatik kaydetmeyi tetikliyor.
        Selected.Depots.Add(new DepotConfig());
    }

    [RelayCommand]
    private void BrowseDepotContent(DepotConfig depot)
    {
        if (dialogs.PickFolder(AppLocalizer.Instance.Get("Depot.ContentRoot"), depot.ContentRoot) is { } picked)
        {
            depot.ContentRoot = picked;
        }
    }

    [RelayCommand]
    private void BrowseSubAppContent()
    {
        if (Selected is null)
        {
            return;
        }

        if (dialogs.PickFolder(AppLocalizer.Instance.Get("General.SharedContent"), Selected.ContentRoot) is { } picked)
        {
            Selected.ContentRoot = picked;
        }
    }

    /// <summary>
    /// Depot siler. Yanlışlıkla silinen bir depot build'i bozacağı (ve eksik depot
    /// Steam'de fark edilmeden yayına çıkabileceği) için onay isteniyor.
    /// </summary>
    [RelayCommand]
    private async Task RemoveDepotAsync(DepotConfig depot)
    {
        if (Selected is not { } subApp)
        {
            return;
        }

        var label = depot.Label is { Length: > 0 } named
            ? $"{depot.DepotId} · {named}"
            : depot.DepotId.ToString();

        var confirmed = await confirmation.ConfirmAsync(
            AppLocalizer.Instance.Get("Depot.Delete.Title"),
            AppLocalizer.Instance.Format("Depot.Delete.Body", label),
            confirmText: AppLocalizer.Instance.Get("Common.Delete"),
            isDestructive: true);

        if (!confirmed)
        {
            return;
        }

        subApp.Depots.Remove(depot);
    }

}

/// <summary>Build açıklaması şablonundaki yer tutucuları doldurur.</summary>
public static class BuildDescription
{
    public static string Render(string template, SteamApp app, SubApp subApp, UserProfile profile)
    {
        var now = DateTimeOffset.Now;

        return template
            .Replace("{app}", app.Title)
            .Replace("{subapp}", subApp.Title)
            .Replace("{kind}", subApp.Kind.ToString())
            .Replace("{appid}", subApp.SteamAppId.ToString())
            .Replace("{branch}", subApp.SetLiveBranch)
            .Replace("{user}", profile.SteamUsername)
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"));
    }
}
