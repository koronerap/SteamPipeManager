using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private string _scriptPreview = "";

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public bool HasSelection => Selected is not null;

    public bool CanBuild => Issues.Count == 0 && Selected is not null;

    public string AppTitle => Navigation.App?.Title ?? "";

    private bool _refreshing;

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

    partial void OnSelectedCardChanged(SubAppCard? value)
    {
        Navigation.SubApp = value?.SubApp;
        Validate();
        UpdatePreview();
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        await repository.SaveAsync();
        Validate();
        UpdatePreview();
        StatusMessage = AppLocalizer.Instance.Get("Settings.Saved");
    }

    [RelayCommand]
    private void AddDepot()
    {
        Selected?.Depots.Add(new DepotConfig());
        Validate();
        UpdatePreview();
    }

    [RelayCommand]
    private void BrowseDepotContent(DepotConfig depot)
    {
        if (dialogs.PickFolder(AppLocalizer.Instance.Get("Depot.ContentRoot"), depot.ContentRoot) is { } picked)
        {
            depot.ContentRoot = picked;
            Validate();
            UpdatePreview();
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
            Validate();
            UpdatePreview();
        }
    }

    [RelayCommand]
    private void RemoveDepot(DepotConfig depot)
    {
        Selected?.Depots.Remove(depot);
        Validate();
        UpdatePreview();
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
