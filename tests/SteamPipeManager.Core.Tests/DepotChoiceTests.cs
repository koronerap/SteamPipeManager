using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Build'e basınca hangi depot'ların gönderileceği soruluyor. Hepsi işaretli başlıyor;
/// gönderilemeyecek olanlar (ör. o platformun klasörü boş) işaretsiz ve nedeni yazılı.
/// Bu testler build çalıştırmıyor; yalnızca seçimin kendisini sınıyor.
/// </summary>
public sealed class DepotChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_depots_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Content(string name, bool empty = false)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        if (!empty)
        {
            File.WriteAllText(Path.Combine(dir, "game.bin"), "oyun");
        }

        return dir;
    }

    private BuildPanelViewModel Panel(bool withProfile, params DepotConfig[] depots)
    {
        var layout = new WorkspaceLayout(Path.Combine(_root, "workspace"));
        var secrets = Path.Combine(_root, "secrets.json");

        var coordinator = new BuildCoordinator(
            layout,
            new JsonSettingsStore(layout),
            new SteamCmdProvisioner(),
            new EpicSecretStore(secrets, new OwnerOnlyFileBackend(secrets)));

        var subApp = new SubApp { Title = "Ana Oyun", SteamAppId = 1000, Depots = [.. depots] };
        var app = new SteamApp { Title = "Test Oyunu", SubApps = [subApp] };
        var profile = new UserProfile { DisplayName = "Test", SteamUsername = "tester", Apps = [app] };

        var navigation = new NavigationState();

        if (withProfile)
        {
            navigation.SelectProfile(profile);
            navigation.SelectApp(app);
        }

        navigation.SubApp = subApp;

        var panel = new BuildPanelViewModel(coordinator, navigation, new NeverConfirm(), new BuildHistoryStore(layout));
        panel.OnTargetChanged(ProfileValidator.ValidateSubApp(subApp).CanBuild);
        return panel;
    }

    private DepotConfig Depot(uint id, string label, bool empty = false) =>
        new() { DepotId = id, Label = label, ContentRoot = Content(label, empty) };

    [Fact]
    public void Building_a_target_with_several_depots_asks_which_with_all_checked()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows"), Depot(1002, "Mac"));

        panel.BuildCommand.Execute(null);

        Assert.True(panel.IsChoosingDepots);
        Assert.Equal([1001u, 1002u], panel.DepotChoices.Select(c => c.Depot.DepotId));
        Assert.All(panel.DepotChoices, c => Assert.True(c.IsSelected));
        Assert.Equal(2, panel.SelectedDepotCount);

        // Seçim açıkken başka bir build başlatılamıyor.
        Assert.False(panel.CanBuild);
        Assert.False(panel.CanPreview);
    }

    [Fact]
    public void Unchecking_updates_the_count_and_nothing_checked_disables_the_build()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows"), Depot(1002, "Mac"));
        panel.BuildCommand.Execute(null);

        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        panel.DepotChoices[1].IsSelected = false;

        Assert.Equal(1, panel.SelectedDepotCount);
        Assert.True(panel.HasSelectedDepots);
        Assert.Contains(nameof(BuildPanelViewModel.UploadSelectedText), changed);

        panel.DepotChoices[0].IsSelected = false;

        Assert.False(panel.HasSelectedDepots);
    }

    /// <summary>
    /// Mac klasörü boşken Windows yine gönderilebilmeli. Önizleme bütün depot'ları
    /// gönderdiği için o kapalı kalıyor.
    /// </summary>
    [Fact]
    public void A_depot_that_cannot_be_sent_is_unchecked_explained_and_does_not_block_the_rest()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows"), Depot(1002, "Mac", empty: true));

        Assert.False(panel.TargetIsValid);
        Assert.True(panel.CanBuild);
        Assert.False(panel.CanPreview);

        panel.BuildCommand.Execute(null);

        var windows = panel.DepotChoices[0];
        var mac = panel.DepotChoices[1];

        Assert.True(windows.IsSelected);
        Assert.False(mac.IsAvailable);
        Assert.False(mac.IsSelected);
        Assert.False(string.IsNullOrEmpty(mac.Problem));
        Assert.Equal(1, panel.SelectedDepotCount);
    }

    [Fact]
    public void When_no_depot_can_be_sent_the_build_stays_disabled()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows", empty: true), Depot(1002, "Mac", empty: true));

        Assert.False(panel.CanBuild);
    }

    [Fact]
    public void Cancelling_closes_the_choice()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows"), Depot(1002, "Mac"));
        panel.BuildCommand.Execute(null);

        panel.CancelDepotChoiceCommand.Execute(null);

        Assert.False(panel.IsChoosingDepots);
        Assert.True(panel.CanBuild);
    }

    [Fact]
    public void Switching_targets_closes_the_choice()
    {
        var panel = Panel(withProfile: true, Depot(1001, "Windows"), Depot(1002, "Mac"));
        panel.BuildCommand.Execute(null);

        panel.OnTargetChanged(isValid: true);

        Assert.False(panel.IsChoosingDepots);
    }

    /// <summary>Tek depot'lu hedefte sorulacak bir şey yok. (Profil yok: build gerçekten başlamasın.)</summary>
    [Fact]
    public void A_single_depot_target_is_not_asked()
    {
        var panel = Panel(withProfile: false, Depot(1001, "Windows"));

        panel.BuildCommand.Execute(null);

        Assert.False(panel.IsChoosingDepots);
        Assert.Empty(panel.DepotChoices);
    }
}

file sealed class NeverConfirm : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        Task.FromResult(false);
}
