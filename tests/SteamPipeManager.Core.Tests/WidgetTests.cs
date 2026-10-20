using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Widget: hangi oyunların şeritte göründüğü ve ayarların kendi dosyasında kalıcı olması.
/// Ağa çıkılmıyor; önbellek boş, sayılar "—".
/// </summary>
public sealed class WidgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_widget_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkspaceLayout Layout => new(_root);

    private async Task<WidgetViewModel> WidgetWithGamesAsync(params (string Title, uint AppId, SubAppKind Kind)[] games)
    {
        var profile = new UserProfile
        {
            DisplayName = "p",
            Apps = [.. games.Select(g => new SteamApp
            {
                Title = g.Title,
                SubApps = [new SubApp { Title = g.Title, SteamAppId = g.AppId, Kind = g.Kind }],
            })],
        };

        await new JsonProfileStore(Layout).SaveAsync(new ProfileDatabase { Profiles = [profile] });

        var http = new HttpClient(new FakeSteamApi { ValidKey = "k" });
        var service = new AnalyticsService(
            new AnalyticsStore(Layout.AnalyticsDirectory),
            new SteamPlayerCountClient(http),
            new SteamFinancialClient(http));

        var keys = new SteamFinancialKeyStore("unused", new MemoryBackend());
        var widget = new WidgetViewModel(service, new ProfileRepository(new JsonProfileStore(Layout)), keys, Layout);
        await widget.LoadAsync();
        return widget;
    }

    [Fact]
    public async Task By_default_the_strip_shows_the_first_three_main_games()
    {
        var widget = await WidgetWithGamesAsync(
            ("Alpha", 1, SubAppKind.Main), ("Alpha Demo", 2, SubAppKind.Demo), ("Beta", 3, SubAppKind.Main),
            ("Gamma", 4, SubAppKind.Main), ("Delta", 5, SubAppKind.Main));

        Assert.Equal([1u, 3u, 5u], widget.Items.Select(i => i.AppId).Order());
        Assert.Equal(3, widget.Items.Count);
        Assert.Contains("Pipe Manager Pulse", widget.Tooltip);
    }

    [Fact]
    public async Task Pinning_is_saved_in_its_own_file_and_survives_a_restart()
    {
        var widget = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main), ("Alpha Demo", 2, SubAppKind.Demo));

        await widget.TogglePinnedAsync(2);
        await widget.ToggleMetricAsync(WidgetMetric.Sales);

        Assert.True(File.Exists(Layout.WidgetSettingsFile));
        Assert.False(File.Exists(Layout.SettingsFile));

        var restarted = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main), ("Alpha Demo", 2, SubAppKind.Demo));

        Assert.Equal([1u, 2u], restarted.Items.Select(i => i.AppId));
        Assert.False(restarted.Settings.ShowSales);

        await restarted.TogglePinnedAsync(1);
        Assert.Equal([2u], restarted.Items.Select(i => i.AppId));
    }

    [Fact]
    public async Task Long_titles_are_shortened_for_the_strip()
    {
        var widget = await WidgetWithGamesAsync(("The Extraordinarily Long Adventure", 1, SubAppKind.Main));

        Assert.Equal("The Extraordinari…", widget.Items[0].ShortTitle);
        Assert.Equal("—", widget.Items[0].PlayersText);
        Assert.False(widget.Items[0].ShowWishlist);
    }
}
