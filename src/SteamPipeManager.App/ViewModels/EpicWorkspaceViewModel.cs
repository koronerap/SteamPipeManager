using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.App.Localization;
using SteamPipeManager.App.Services;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.App.ViewModels;

/// <summary>Sol listedeki tek Epic hedefi.</summary>
public sealed partial class EpicArtifactCard(EpicArtifact artifact) : ObservableObject
{
    public EpicArtifact Artifact { get; } = artifact;

    public string Title => Artifact.Title;

    public string Subtitle => Artifact.ArtifactId is { Length: > 0 } id
        ? $"{Artifact.Kind} · {id}"
        : Artifact.Kind.ToString();

    public string Initial => Artifact.Title is { Length: > 0 } title
        ? title[..1].ToUpperInvariant()
        : "?";

    public void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Initial));
    }
}

/// <summary>
/// Epic hedeflerinin (artifact) yönetim ekranı.
///
/// Steam'in çalışma alanından ayrı bir ekran, çünkü içerik modeli farklı: depot yok,
/// artifact başına tek içerik klasörü var; canlıya alma script alanı değil ayrı bir
/// çağrı; ve script üretimi yerine <b>çalıştırılacak komut satırı</b> önizleniyor.
///
/// Steam ekranını koşullu sekmelerle esnetmek yerine ayrı tutuluyor — iki ekranın da
/// kendi doğal hâlinde kalması, kullanıcıya da bakım açısından da daha temiz.
/// </summary>
public sealed partial class EpicWorkspaceViewModel(
    ProfileRepository repository,
    NavigationState navigation,
    IDialogService dialogs,
    IConfirmationService confirmation,
    EpicSecretStore secrets,
    EpicBuildPanelViewModel buildPanel) : ObservableObject
{
    public NavigationState Navigation { get; } = navigation;

    public EpicBuildPanelViewModel BuildPanel { get; } = buildPanel;

    public ObservableCollection<EpicArtifactCard> Artifacts { get; } = [];

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(Selected))]
    private EpicArtifactCard? _selectedCard;

    public EpicArtifact? Selected => SelectedCard?.Artifact;

    public bool HasSelection => Selected is not null;

    public bool CanBuild => Issues.Count == 0 && Selected is not null;

    /// <summary>Sekmelerdeki Build'in sırası; hızlı build oraya geçiyor.</summary>
    public const int BuildTabIndex = 2;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _commandPreview = "";

    [ObservableProperty]
    private string? _statusMessage;

    public string AppTitle => Navigation.App?.Title ?? "";

    /// <summary>Oyunun Epic ayarları; yoksa oluşturuluyor (Epic profilindeki her oyunun var).</summary>
    public EpicGameSettings? Game => Navigation.App?.Epic;

    public string ProductId
    {
        get => Game?.ProductId ?? "";
        set
        {
            if (Game is { } game && game.ProductId != value)
            {
                game.ProductId = value;
                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public IReadOnlyList<SubAppKind> Kinds { get; } =
        [.. Enum.GetValues<SubAppKind>()];

    private bool _refreshing;
    private DebouncedSaver? _saver;

    /// <summary>
    /// Otomatik kayıt. Her tuş vuruşunda diske yazmamak için kısa bir bekleme var;
    /// sayfadan çıkarken, hedef değiştirirken ve kapanışta <see cref="FlushAsync"/>
    /// bekleyeni hemen yazıyor.
    /// </summary>
    private DebouncedSaver Saver => _saver ??= CreateSaver();

    public TimeSpan AutoSaveDelay
    {
        get => Saver.Delay;
        set => Saver.Delay = value;
    }

    /// <summary>Diske yazılmamış düzenleme var mı; kapanışta sorulur.</summary>
    public bool HasUnsavedChanges => Saver.HasUnsavedChanges;

    /// <summary>Son kaydın hatası; kapanışta kullanıcıya gösterilir.</summary>
    public string? LastSaveError => Saver.LastError?.Message;

    private DebouncedSaver CreateSaver()
    {
        var saver = new DebouncedSaver(() => repository.SaveAsync(), TimeSpan.FromMilliseconds(400));

        // Hata sessizce yutulmasın: "otomatik kaydedildi" yazarken kaydedilmemesi,
        // kullanıcının değişikliğini ancak yeniden açınca kaybettiğini fark etmesi demek.
        saver.Completed += error => StatusMessage = error is null
            ? null
            : AppLocalizer.Instance.Format("Common.SaveFailed", error.Message);

        return saver;
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
            // Epic profilindeki bir oyunun Epic ayarları olmalı; eski ya da elle
            // düzenlenmiş dosyalarda eksik olabilir.
            if (Navigation.App is { Epic: null } app)
            {
                app.Epic = new EpicGameSettings();
            }

            Artifacts.Clear();

            foreach (var artifact in Game?.Artifacts ?? [])
            {
                Artifacts.Add(new EpicArtifactCard(artifact));
            }

            SelectedCard = Artifacts.FirstOrDefault();

            OnPropertyChanged(nameof(AppTitle));
            OnPropertyChanged(nameof(Game));
            OnPropertyChanged(nameof(ProductId));
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedCardChanged(EpicArtifactCard? oldValue, EpicArtifactCard? newValue)
    {
        _ = FlushAsync();

        Unhook(oldValue?.Artifact);
        Hook(newValue?.Artifact);

        Validate();
        UpdatePreview();
    }

    private void Hook(EpicArtifact? artifact)
    {
        if (artifact is not null)
        {
            artifact.PropertyChanged += OnModelChanged;
        }
    }

    private void Unhook(EpicArtifact? artifact)
    {
        if (artifact is not null)
        {
            artifact.PropertyChanged -= OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    public void Validate()
    {
        Issues.Clear();

        if (Selected is not { } artifact)
        {
            OnPropertyChanged(nameof(CanBuild));
            return;
        }

        foreach (var issue in EpicArtifactValidator.Validate(
                     artifact, Navigation.Profile?.Epic, Game).Issues)
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(CanBuild));
        BuildPanel.OnTargetChanged(Selected, CanBuild);
    }

    /// <summary>
    /// Steam'deki "script önizleme"nin karşılığı: Epic'te script üretilmiyor, onun
    /// yerine <b>çalıştırılacak komut satırı</b> gösteriliyor.
    ///
    /// Client secret komut satırında zaten yok — ortam değişkeniyle geçiriliyor — bu
    /// yüzden önizlemede maskelenecek bir şey de yok; kullanıcı ne çalışacağını
    /// birebir görüyor.
    /// </summary>
    public void UpdatePreview()
    {
        if (Selected is not { } artifact ||
            Navigation.Profile?.Epic is not { } profile ||
            Game is not { } game)
        {
            CommandPreview = "";
            return;
        }

        var version = EpicBuildVersion.NextUnique(
            artifact.BuildVersionTemplate, [], AppTitle, artifact.Title,
            artifact.Kind, artifact.ArtifactId);

        var upload = new BptCommand("UploadBinary")
            .WithCredentials(profile.OrganizationId, game.ProductId, artifact.ArtifactId,
                profile.ClientId, "")
            .Add("BuildRoot", artifact.BuildRoot)
            .Add("BuildVersion", version)
            .Add("AppLaunch", artifact.AppLaunch)
            .Add("AppArgs", artifact.AppArgs)
            .Add("CloudDir", artifact.CloudDir);

        if (artifact.Preview)
        {
            upload.AddFlag("DryRun");
        }

        var sections = new List<string>
        {
            $"=== {AppLocalizer.Instance.Get("Epic.Step.Upload")} ===",
            upload.ToDisplayString("BuildPatchTool.exe"),
        };

        if (artifact.Label is { Length: > 0 } && !artifact.Preview)
        {
            var label = new BptCommand("LabelBinary")
                .WithCredentials(profile.OrganizationId, game.ProductId, artifact.ArtifactId,
                    profile.ClientId, "")
                .Add("BuildVersion", version)
                .Add("Label", artifact.Label)
                .Add("Platform", artifact.Platform)
                .Add("SandboxId", artifact.SandboxId);

            sections.Add("");
            sections.Add($"=== {AppLocalizer.Instance.Get("Epic.Step.Label")} ===");
            sections.Add(label.ToDisplayString("BuildPatchTool.exe"));
        }

        // Ölçüldü (Bulgu 16): BuildPatchTool sürüm dizesindeki boşlukları siliyor.
        // Komut satırı olduğu gibi doğru, ama Epic'e kaydedilen sürüm farklı olacak —
        // kullanıcı bunu görmezse "1.0 beta" ile "1.0beta"yı ayrı sanır.
        if (EpicBuildVersion.Normalize(version) != version)
        {
            sections.Add("");
            sections.Add(AppLocalizer.Instance.Format(
                "Epic.Preview.VersionNote", EpicBuildVersion.Normalize(version)));
        }

        sections.Add("");
        sections.Add(AppLocalizer.Instance.Get("Epic.Preview.SecretNote"));

        CommandPreview = string.Join(Environment.NewLine, sections);
    }

    // --- Kaydetme ---

    /// <summary>
    /// Arayüzdeki her düzenleme buradan geçer: doğrulama ve önizleme anında
    /// güncellenir, diske yazma kısa bir gecikmeyle yapılır.
    /// </summary>
    public void ScheduleSave()
    {
        Validate();
        UpdatePreview();
        SelectedCard?.NotifyModelChanged();

        Saver.Schedule();
    }

    /// <summary>
    /// Bekleyen otomatik kaydı hemen diske yazar; yazılacak bir şey yoksa dokunmaz.
    /// Hedef değiştirilirken, sayfadan çıkılırken ve uygulama kapanırken çağrılır.
    /// </summary>
    public Task FlushAsync() => Saver.FlushAsync();

    // --- Komutlar ---

    /// <summary>Bkz. <see cref="SubAppWorkspaceViewModel.QuickBuildCommand"/>.</summary>
    [RelayCommand]
    private void QuickBuild(EpicArtifactCard? card)
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
    private async Task AddArtifactAsync()
    {
        if (Navigation.App is not { } app)
        {
            return;
        }

        app.Epic ??= new EpicGameSettings();

        var artifact = new EpicArtifact
        {
            Title = AppLocalizer.Instance.Get("Epic.NewArtifact"),
            Kind = app.Epic.Artifacts.Any(a => a.Kind == SubAppKind.Main)
                ? SubAppKind.Demo
                : SubAppKind.Main,
        };

        app.Epic.Artifacts.Add(artifact);
        await repository.SaveAsync();

        Refresh();
        SelectedCard = Artifacts.FirstOrDefault(c => c.Artifact == artifact);
    }

    [RelayCommand]
    private async Task DeleteArtifactAsync(EpicArtifactCard? card)
    {
        if ((card ?? SelectedCard) is not { } target || Game is not { } game)
        {
            return;
        }

        var confirmed = await confirmation.ConfirmAsync(
            AppLocalizer.Instance.Get("Epic.Delete.Title"),
            AppLocalizer.Instance.Format(
                "Epic.Delete.Body", target.Title, target.Artifact.ArtifactId),
            confirmText: AppLocalizer.Instance.Get("Common.Delete"),
            isDestructive: true);

        if (!confirmed)
        {
            return;
        }

        game.Artifacts.Remove(target.Artifact);
        await repository.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private void BrowseBuildRoot()
    {
        if (Selected is not { } artifact)
        {
            return;
        }

        if (dialogs.PickFolder(AppLocalizer.Instance.Get("Epic.BuildRoot"), artifact.BuildRoot) is { } picked)
        {
            artifact.BuildRoot = picked;
        }
    }

    [RelayCommand]
    private void BrowseCloudDir()
    {
        if (Selected is not { } artifact)
        {
            return;
        }

        if (dialogs.PickFolder(AppLocalizer.Instance.Get("Epic.CloudDir"), artifact.CloudDir) is { } picked)
        {
            artifact.CloudDir = picked;
        }
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

    /// <summary>Profilde secret kayıtlı mı — arayüzde uyarı göstermek için.</summary>
    public bool HasSecret =>
        Navigation.Profile is { } profile && secrets.Has(profile.Id);
}
