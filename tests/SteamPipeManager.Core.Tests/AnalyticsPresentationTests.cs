using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Core.Tests;

/// <summary>Analitik sayfasının ve widget'ın ortak yardımcıları.</summary>
public sealed class AnalyticsPresentationTests
{
    [Fact]
    public void Games_come_from_steam_profiles_once_each_main_games_first()
    {
        UserProfile Profile(PublishProviderId provider, params SteamApp[] apps) =>
            new() { DisplayName = "p", Provider = provider, Apps = [.. apps] };

        SteamApp Game(string title, params SubApp[] targets) => new() { Title = title, SubApps = [.. targets] };

        var steam = Profile(PublishProviderId.Steam,
            Game("Zeta", new SubApp { Title = "Zeta Demo", Kind = SubAppKind.Demo, SteamAppId = 11 }, new SubApp { Title = "Zeta", Kind = SubAppKind.Main, SteamAppId = 10 }),
            Game("Alpha", new SubApp { Title = "Alpha", Kind = SubAppKind.Main, SteamAppId = 20 }, new SubApp { Title = "Yeni", Kind = SubAppKind.Main, SteamAppId = 0 }));

        // Aynı oyun ikinci bir profilde; Epic profili sayılmıyor.
        var other = Profile(PublishProviderId.Steam, Game("Alpha", new SubApp { Title = "Alpha", Kind = SubAppKind.Main, SteamAppId = 20 }));
        var epic = Profile(PublishProviderId.Epic, Game("Epic Game", new SubApp { Title = "x", Kind = SubAppKind.Main, SteamAppId = 30 }));

        var targets = AnalyticsTargets.From([steam, other, epic]);

        Assert.Equal([20u, 10u, 11u], targets.Select(t => t.AppId));
        Assert.Equal("Zeta — Zeta Demo", targets[2].Title);
    }

    [Fact]
    public void Bars_scale_sales_from_zero_and_wishlists_from_their_lowest_point()
    {
        Assert.Equal([0.5, 1.0, 0.0], AnalyticsFormat.Bars([5, 10, 0], fromZero: true));

        // Birikimli wishlist: 1000'den 1010'a; en düşük gün yine ince bir çizgi.
        var wishlist = AnalyticsFormat.Bars([1000, 1005, 1010], fromZero: false);
        Assert.Equal(0.06, wishlist[0]);
        Assert.Equal(0.5, wishlist[1]);
        Assert.Equal(1.0, wishlist[2]);

        Assert.Empty(AnalyticsFormat.Bars([0, 0, 0], fromZero: true));
    }

    [Fact]
    public void Player_samples_fall_into_half_hour_slots_keeping_the_highest()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        PlayerSample At(double hoursAgo, int count) => new((now - TimeSpan.FromHours(hoursAgo)).ToUnixTimeSeconds(), count);

        var slots = AnalyticsFormat.HalfHours([At(0.1, 50), At(0.2, 80), At(23.9, 10), At(30, 999)], now).ToList();

        Assert.Equal(48, slots.Count);
        Assert.Equal(10, slots[0]);
        Assert.Equal(80, slots[^1]);
        Assert.DoesNotContain(999, slots);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1234, "1.2K")]
    [InlineData(45678, "46K")]
    [InlineData(1_250_000, "1.3M")]
    public void Compact_numbers_fit_the_widget(int value, string expected)
    {
        using var culture = new CultureScope("en-US");
        Assert.Equal(expected, AnalyticsFormat.Compact(value));
    }

    [Fact]
    public void Changes_carry_their_sign()
    {
        using var culture = new CultureScope("en-US");
        Assert.Equal("+1,200", AnalyticsFormat.Signed(1200));
        Assert.Equal("−3", AnalyticsFormat.Signed(-3));
        Assert.Equal("0", AnalyticsFormat.Signed(0));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
