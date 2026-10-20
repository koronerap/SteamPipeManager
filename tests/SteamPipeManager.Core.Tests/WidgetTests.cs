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

    /// <summary>Önceki sürümün widget.json'ında görünüm ayarları yok: şerit eskisi gibi görünmeli.</summary>
    [Fact]
    public async Task A_settings_file_from_before_the_appearance_options_keeps_the_old_look()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Layout.WidgetSettingsFile,
            """{ "schemaVersion": 1, "showSales": false, "stripRight": 1900, "stripBottom": 1040 }""");

        var widget = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main));
        var settings = widget.Settings;

        Assert.False(settings.ShowSales);
        Assert.Equal(1900, settings.StripRight);
        Assert.True(settings.ShowTitles);
        Assert.True(settings.AlwaysOnTop);
        Assert.False(settings.LockPosition);
        Assert.Equal(WidgetTextSize.Normal, settings.TextSize);
        Assert.Equal(WidgetOrientation.Horizontal, settings.Orientation);
        Assert.Equal(WidgetTransparency.Low, settings.Transparency);
        Assert.Equal(WidgetTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task Appearance_changes_are_saved_and_survive_a_restart()
    {
        var widget = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main));

        await widget.UpdateAsync(s =>
        {
            s.TextSize = WidgetTextSize.ExtraLarge;
            s.Orientation = WidgetOrientation.Vertical;
            s.AlwaysOnTop = false;
            s.Transparency = WidgetTransparency.High;
            s.Theme = WidgetTheme.System;
            s.LockPosition = true;
        });
        await widget.ToggleMetricAsync(WidgetMetric.Titles);

        Assert.False(widget.Items[0].ShowTitle);

        var restarted = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main));
        var settings = restarted.Settings;

        Assert.Equal(WidgetTextSize.ExtraLarge, settings.TextSize);
        Assert.Equal(WidgetOrientation.Vertical, settings.Orientation);
        Assert.False(settings.AlwaysOnTop);
        Assert.Equal(WidgetTransparency.High, settings.Transparency);
        Assert.Equal(WidgetTheme.System, settings.Theme);
        Assert.True(settings.LockPosition);
        Assert.False(restarted.IsShown(WidgetMetric.Titles));
        Assert.False(restarted.Items[0].ShowTitle);

        // Ayarlar okunur hâlde: enum'lar adlarıyla.
        Assert.Contains("\"ExtraLarge\"", await File.ReadAllTextAsync(Layout.WidgetSettingsFile));
    }

    [Fact]
    public async Task Resetting_the_position_forgets_where_the_strip_was_dropped()
    {
        var widget = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main));
        await widget.UpdateAsync(s => (s.StripRight, s.StripBottom) = (500, 400));

        await widget.ResetPositionAsync();

        var restarted = await WidgetWithGamesAsync(("Alpha", 1, SubAppKind.Main));
        Assert.Null(restarted.Settings.StripRight);
        Assert.Null(restarted.Settings.StripBottom);
    }

    [Fact]
    public void Larger_text_scales_up_and_more_transparency_means_a_fainter_background()
    {
        var scales = Enum.GetValues<WidgetTextSize>().Select(WidgetLook.Scale).ToList();
        var opacities = Enum.GetValues<WidgetTransparency>().Select(WidgetLook.BackgroundOpacity).ToList();

        Assert.Equal(scales.Order(), scales);
        Assert.Equal(1.0, WidgetLook.Scale(WidgetTextSize.Normal));
        Assert.Equal(opacities.OrderDescending(), opacities);
        Assert.Equal(1.0, opacities[0]);

        // En saydamı bile görünür: yazılar okunsun, şerit tutulabilsin.
        Assert.True(opacities[^1] >= 0.4);
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
