using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Presentation.Charts;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Analitik grafikleri: geçmişin aralıklara bölünmesi, eksenler, boşluklar ve imleç.
/// Çizim yok; WPF ve Avalonia'nın çizdiği yerleşim burada hesaplanıyor.
/// </summary>
public sealed class AnalyticsChartTests : IDisposable
{
    private const uint Game = 4000;

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_charts_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static AppHistory History(
        IEnumerable<PlayerSample>? samples = null, IEnumerable<PlayerHour>? hours = null,
        WishlistHistory? wishlist = null, SalesHistory? sales = null) =>
        new(Now, [.. samples ?? []], [.. hours ?? []], wishlist, sales);

    /// <summary>Her 5 dakikada bir örnek, <paramref name="from"/>'dan <paramref name="to"/>'ya (şimdiden geriye saat).</summary>
    private static IEnumerable<PlayerSample> Samples(double fromHoursAgo, double toHoursAgo, int count = 100)
    {
        for (var t = Now.AddHours(-fromHoursAgo); t <= Now.AddHours(-toHoursAgo); t = t.AddMinutes(5))
        {
            yield return new PlayerSample(t.ToUnixTimeSeconds(), count);
        }
    }

    // ---------------------------------------------------------------- eksen ve yerleşim

    [Fact]
    public void Axis_ticks_are_round_numbers_that_cover_every_value()
    {
        Assert.Equal([0d, 500, 1000, 1500], ChartLayout.NiceTicks(0, 1234).Ticks);
        Assert.Equal([-50d, 0, 50, 100, 150], ChartLayout.NiceTicks(-30, 120).Ticks);

        // Tek değer: çizgi ortada, eksen yine okunur; adette kesirli işaret yok.
        Assert.Equal([4d, 5, 6], ChartLayout.NiceTicks(5, 5).Ticks);
    }

    /// <summary>Dar aralıkta kısa biçim aynı yazıyı tekrarlardı (36K, 36K, 36K); tam sayıya geçiliyor.</summary>
    [Fact]
    public void Axis_labels_fall_back_to_exact_numbers_when_the_short_form_repeats()
    {
        var start = new DateTime(2026, 10, 1);
        var model = new ChartModel(start, start.AddDays(2),
            [new ChartSeries("Total", ChartStyle.Line, ChartTone.Wishlist,
                [new(start, start.AddDays(1), 35_210), new(start.AddDays(1), start.AddDays(2), 36_080)])],
            ChartValueKind.Count, ChartTimeUnit.Days) { FromZero = false };

        var labels = ChartLayout.Compute(model, 400, 200, s => s.Length * 6).YTicks.Select(t => t.Label).ToList();

        Assert.True(labels.Count >= 3);
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.DoesNotContain(labels, l => l.EndsWith('K'));
    }

    private static ChartModel TwoSeries(DateTime start)
    {
        ChartBucket Day(int i, double? value) => new(start.AddDays(i), start.AddDays(i + 1), value);

        return new ChartModel(
            start,
            start.AddDays(4),
            [
                new ChartSeries("Line", ChartStyle.Line, ChartTone.Players, [Day(0, 1), Day(1, null), Day(2, 3), Day(3, 4)]),
                new ChartSeries("Bars", ChartStyle.Bars, ChartTone.Sales, [Day(0, 2), Day(1, -1), Day(2, null), Day(3, 0)]),
            ],
            ChartValueKind.Count,
            ChartTimeUnit.Days);
    }

    [Fact]
    public void Lines_break_where_there_is_no_data_and_bars_rise_from_zero()
    {
        var layout = ChartLayout.Compute(TwoSeries(new DateTime(2026, 10, 1)), 400, 200, s => s.Length * 6);

        var line = Assert.Single(layout.Lines);
        Assert.Equal([1, 2], line.Segments.Select(s => s.Count));

        // Bilinmeyen ve sıfır gün çubuk değil; eksi gün sıfırın altında.
        Assert.Equal(2, layout.Bars.Count);
        var zero = Assert.IsType<double>(layout.ZeroY);
        Assert.Equal(zero, layout.Bars[0].Y + layout.Bars[0].Height, 6);
        Assert.Equal(zero, layout.Bars[1].Y, 6);
        Assert.True(layout.PlotLeft > 0);
        Assert.NotEmpty(layout.XTicks);
    }

    [Fact]
    public void Hovering_picks_the_day_under_the_cursor_and_the_same_day_in_another_chart()
    {
        var start = new DateTime(2026, 10, 1);
        var layout = ChartLayout.Compute(TwoSeries(start), 400, 200, s => s.Length * 6);

        var hover = Assert.IsType<ChartHover>(layout.HoverAt(layout.XOf(start.AddHours(36))));
        Assert.Equal(start.AddDays(1), hover.Time);
        Assert.Equal(2, hover.Rows.Count);
        Assert.EndsWith("—", hover.Rows[0].Text);
        Assert.Null(hover.Rows[1].Y);

        // Başka bir grafikte aynı ana denk gelen gün.
        Assert.Equal(start.AddDays(3), layout.HoverAtTime(start.AddDays(3).AddHours(20))!.Time);

        Assert.Null(layout.HoverAt(layout.PlotLeft - 5));
        Assert.Null(layout.HoverAtTime(start.AddHours(-1)));
    }

    // ---------------------------------------------------------------- oyuncular

    [Fact]
    public void The_last_day_of_players_is_drawn_in_quarter_hours_and_breaks_where_nothing_was_recorded()
    {
        var steady = AnalyticsCharts.Players(History(Samples(6, 0)), AnalyticsRange.Day, compare: false);

        // 24 saat ve içinde bulunulan çeyrek.
        Assert.Equal(97, steady.Model.Series[0].Buckets.Count);
        Assert.Single(steady.Model.Series);
        Assert.Equal(100, steady.Peak);
        Assert.False(steady.HasGaps);

        // Altı saat kayıt, üç saat yok (uygulama kapalı), iki saat kayıt.
        var broken = AnalyticsCharts.Players(History([.. Samples(11, 5), .. Samples(2, 0, count: 300)]), AnalyticsRange.Day, compare: false);

        Assert.True(broken.HasGaps);
        Assert.Equal(300, broken.Peak);
    }

    [Fact]
    public void Longer_ranges_use_the_hourly_summaries_and_show_the_average()
    {
        var hours = Enumerable.Range(31 * 24, 30 * 24)
            .Select(h => new PlayerHour(Now.AddHours(-h).ToUnixTimeSeconds() / 3600 * 3600, 120, 60, 12));

        var history = History(Samples(48, 0, count: 80), hours);
        var chart = AnalyticsCharts.Players(history, AnalyticsRange.Quarter, compare: false);

        Assert.Equal(ChartTimeUnit.Hours, chart.Model.TimeUnit);
        Assert.Equal(120, chart.Peak);
        Assert.True(chart.Model.Series[1].Faint);
        Assert.Equal(60, chart.Model.Series[1].Buckets.Select(b => b.Value).OfType<double>().Min());

        // Önceki dönem şimdiki dönemin üstüne kaydırılmış.
        var compared = AnalyticsCharts.Players(history, AnalyticsRange.Month, compare: true);
        Assert.Equal(compared.Model.Series[0].Buckets[0].Start, compared.Model.Series[1].Buckets[0].Start);
        Assert.Equal(120, compared.Model.Series[1].Buckets.Select(b => b.Value).OfType<double>().Max());
    }

    // ---------------------------------------------------------------- wishlist

    private static WishlistHistory Wishlist(int total, DateOnly since, params (int DaysAgo, int Adds, int Deletes)[] days) =>
        new(
            [.. days.OrderByDescending(d => d.DaysAgo).Select(d => new WishlistDayCounts(Today.AddDays(-d.DaysAgo), new WishlistCounts(d.Adds, d.Deletes, 0, 0)))],
            total, IsComplete: true, since, Today);

    [Fact]
    public void Wishlist_total_is_the_running_total_at_the_end_of_each_day()
    {
        var wishlist = Wishlist(100, Today.AddDays(-2), (2, 12, 2), (1, 5, 0), (0, 3, 0));

        var total = AnalyticsCharts.Wishlist(wishlist, AnalyticsRange.Week, WishlistChartMode.Total, compare: false);
        var values = total.Model.Series[0].Buckets.Select(b => b.Value).ToList();

        Assert.Equal(7, total.Days);
        Assert.Equal([null, null, null, null, 92d, 97d, 100d], values);
        Assert.Equal(18, total.Net);
        Assert.False(total.Model.FromZero);

        var daily = AnalyticsCharts.Wishlist(wishlist, AnalyticsRange.Week, WishlistChartMode.Daily, compare: false);
        Assert.Equal([10d, 5d, 3d], daily.Model.Series[0].Buckets.Skip(4).Select(b => b.Value));
        Assert.NotNull(daily.Model.Series[0].Buckets[4].Detail);
        Assert.Equal(ChartStyle.Bars, daily.Model.Series[0].Style);
    }

    [Fact]
    public void A_wishlist_older_than_four_hundred_days_is_summed_by_week()
    {
        var days = Enumerable.Range(0, 501).Select(i => (i, 1, 0)).ToArray();
        var chart = AnalyticsCharts.Wishlist(Wishlist(501, Today.AddDays(-500), days), AnalyticsRange.All, WishlistChartMode.Daily, compare: false);

        Assert.Equal(ChartTimeUnit.Weeks, chart.Model.TimeUnit);
        Assert.Equal(72, chart.Model.Series[0].Buckets.Count);
        Assert.Equal(7, chart.Model.Series[0].Buckets[0].Value);
    }

    // ---------------------------------------------------------------- satış

    [Fact]
    public void Sales_days_without_rows_are_zero_but_days_still_downloading_are_unknown()
    {
        var sales = new SalesHistory(
            [new SalesDayTotals(Today.AddDays(-1), new SalesTotals(3, 30m, 40m))],
            new HashSet<DateOnly> { Today.AddDays(-3) },
            Today);

        var chart = AnalyticsCharts.Sales(sales, AnalyticsRange.Week, SalesChartMode.Revenue, compare: false);
        var values = chart.Model.Series[0].Buckets.Select(b => b.Value).ToList();

        Assert.Equal([0d, 0d, 0d, null, 0d, 30d, 0d], values);
        Assert.Equal(new SalesTotals(3, 30m, 40m), chart.Totals);
        Assert.Equal(ChartValueKind.Usd, chart.Model.ValueKind);

        // Uzun aralıkta 7 günlük ortalama; adet modunda sayı biçimi.
        var month = AnalyticsCharts.Sales(sales, AnalyticsRange.Month, SalesChartMode.Units, compare: false);
        Assert.Equal(ChartValueKind.Count, month.Model.ValueKind);
        Assert.True(month.Model.Series[1].Faint);
        // Son 7 günün ortalaması; hâlâ inen gün sayılmıyor (3 adet / 6 gün).
        Assert.Equal(0.5, month.Model.Series[1].Buckets[^1].Value!.Value, 6);
    }

    // ---------------------------------------------------------------- seçenekler ve önbellek

    [Fact]
    public void Choosing_an_option_unselects_the_others_and_reports_the_change_once()
    {
        var group = new ChoiceGroup<AnalyticsRange>(AnalyticsRange.Month, v => v.ToString());
        var changes = 0;
        group.Changed += (_, _) => changes++;

        Assert.True(group.Options[2].IsSelected);

        group.Options[0].IsSelected = true;
        group.Options[0].IsSelected = true;

        Assert.Equal(AnalyticsRange.Day, group.Selected);
        Assert.Equal([true, false, false, false, false, false], group.Options.Select(o => o.IsSelected));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task History_comes_from_the_cache_in_order_and_sales_only_for_the_owner()
    {
        var store = new AnalyticsStore(_dir);

        await store.SavePlayersAsync(Game, new PlayerHistory
        {
            Samples = [new PlayerSample(Now.ToUnixTimeSeconds(), 5), new PlayerSample(Now.AddHours(-1).ToUnixTimeSeconds(), 7)],
            Hours = [new PlayerHour(Now.AddDays(-40).ToUnixTimeSeconds(), 9, 8, 12)],
        });

        await store.SaveWishlistAsync(Game, new WishlistLedger
        {
            AppMinDate = Today.AddDays(-1),
            Days = { ["2026-10-20"] = new WishlistRow(3, 0, 0, 0, Today.AddDays(2)), ["2026-10-19"] = new WishlistRow(5, 1, 0, 0, Today.AddDays(2)) },
        });

        await store.SaveSalesAsync("account", new SalesLedger
        {
            LastCheckedAt = Now,
            PendingDates = ["2026-10-18"],
            Days = { ["2026-10-19"] = new() { [Game] = new SalesRow(2, 20m, 25m), [Game + 1] = new SalesRow(9, 90m, 99m) } },
        });

        var http = new HttpClient(new FakeSteamApi { ValidKey = "k" });
        var service = new AnalyticsService(store, new SteamPlayerCountClient(http), new SteamFinancialClient(http), new ManualClock(Now));

        var history = await service.GetHistoryAsync(Game, "account");

        Assert.Equal([7, 5], history.PlayerSamples.Select(s => s.C));
        Assert.Single(history.PlayerHours);
        Assert.Equal([Today.AddDays(-1), Today], history.Wishlist!.Days.Select(d => d.Date));
        Assert.Equal(7, history.Wishlist.Total);
        // Eski biçimdeki satırda pay yok: temel pay (%70) varsayılıyor.
        Assert.Equal(new SalesTotals(2, 20m, 25m, ShareUsd: 14m), Assert.Single(history.Sales!.Days).Totals);
        Assert.Contains(Today.AddDays(-2), history.Sales.Pending);

        Assert.Null((await service.GetHistoryAsync(Game, accountId: null)).Sales);
    }
}
