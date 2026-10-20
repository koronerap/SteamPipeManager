using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Presentation.Charts;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Bir oyunun ayrıntısı: seçilen aralıkta oyuncu, wishlist ve satış grafikleri. Üç grafik
/// aynı tarih ekseninde; imlecin zamanı (<see cref="HoverTime"/>) paylaşılıyor, birinin
/// üstünde durunca ötekiler de o günü gösteriyor.
/// </summary>
public sealed partial class AnalyticsDetailViewModel : ObservableObject
{
    private readonly AnalyticsServices _analytics;
    private readonly string? _accountId;
    private readonly string _currency;
    private readonly Action _close;
    private AppHistory? _history;
    private ExchangeRates? _rates;

    /// <param name="currency">Tutarların yanında gösterilecek para birimi; "USD" ise çeviri yok.</param>
    public AnalyticsDetailViewModel(AnalyticsCard card, AnalyticsServices analytics, string? accountId, string currency, Action close)
    {
        Card = card;
        _analytics = analytics;
        _accountId = accountId;
        _currency = currency;
        _close = close;

        Range = new(AnalyticsRange.Month, v => T($"Analytics.Range.{v}"));
        WishlistMode = new(WishlistChartMode.Total, v => T($"Analytics.WishlistMode.{v}"));
        SalesMode = new(SalesChartMode.Revenue, v => T($"Analytics.SalesMode.{v}"));

        Range.Changed += (_, _) => Rebuild();
        WishlistMode.Changed += (_, _) => Rebuild();
        SalesMode.Changed += (_, _) => Rebuild();
    }

    public AnalyticsCard Card { get; }

    public ChoiceGroup<AnalyticsRange> Range { get; }

    public ChoiceGroup<WishlistChartMode> WishlistMode { get; }

    public ChoiceGroup<SalesChartMode> SalesMode { get; }

    /// <summary>Önceki dönem soluk çizgiyle üst üste.</summary>
    [ObservableProperty]
    private bool _compare;

    /// <summary>İmlecin üstünde durduğu zaman; üç grafik bunu iki yönlü bağlıyor.</summary>
    [ObservableProperty]
    private DateTime? _hoverTime;

    [ObservableProperty]
    private ChartModel? _playersChart;

    [ObservableProperty]
    private string _playersSummary = "";

    [ObservableProperty]
    private bool _hasPlayerGaps;

    [ObservableProperty]
    private bool _hasWishlist;

    [ObservableProperty]
    private ChartModel? _wishlistChart;

    [ObservableProperty]
    private string _wishlistSummary = "";

    [ObservableProperty]
    private string _wishlistCaption = "";

    [ObservableProperty]
    private bool _hasSales;

    [ObservableProperty]
    private ChartModel? _salesChart;

    [ObservableProperty]
    private string _salesSummary = "";

    [ObservableProperty]
    private string _salesCaption = "";

    /// <summary>Kazanç kartı: bugün, dün, bu ay, ay sonu tahmini.</summary>
    [ObservableProperty]
    private IReadOnlyList<EarningsTile> _earningsTiles = [];

    /// <summary>Seçili aralıkta brütten tahmini ödemeye.</summary>
    [ObservableProperty]
    private IReadOnlyList<EarningsRow> _earningsRows = [];

    [ObservableProperty]
    private string _earningsCaption = "";

    [ObservableProperty]
    private string _earningsNote = "";

    public async Task LoadAsync()
    {
        _history = await _analytics.Service.GetHistoryAsync(Card.AppId, _accountId);

        // Kurlar ilk satış gününden; önbellekte yoksa tek istekte iniyor.
        if (_history.Sales is { } sales)
        {
            var from = sales.Days.Count > 0 ? sales.Days[0].Date : sales.Today;
            _rates = await _analytics.Rates.GetAsync(_currency, from);
        }

        Rebuild();
    }

    /// <summary>Tutarlar gizlenip gösterilince metinler yeniden yazılıyor.</summary>
    public void Refresh() => Rebuild();

    partial void OnCompareChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        if (_history is not { } history)
        {
            return;
        }

        var range = Range.Selected;

        var players = AnalyticsCharts.Players(history, range, Compare);
        PlayersChart = players.Model;
        HasPlayerGaps = players.HasGaps;
        PlayersSummary = players.Peak is { } peak
            ? AppLocalizer.Instance.Format("Analytics.Chart.PlayersSummary", AnalyticsFormat.Count(peak), AnalyticsFormat.Count(players.Average))
            : T("Analytics.Chart.NoData");

        HasWishlist = history.Wishlist is not null;

        if (history.Wishlist is { } wishlist)
        {
            var chart = AnalyticsCharts.Wishlist(wishlist, range, WishlistMode.Selected, Compare);
            WishlistChart = chart.Model;
            WishlistSummary = AppLocalizer.Instance.Format(
                "Analytics.Chart.WishlistSummary", AnalyticsFormat.Signed(chart.Net), AnalyticsFormat.Count(wishlist.Total));
            WishlistCaption = AppLocalizer.Instance.Format("Analytics.Chart.DaysGmt", chart.Days) +
                              (wishlist.IsComplete ? "" : " · " + T("Analytics.WishlistIncomplete"));
        }

        HasSales = history.Sales is not null;

        if (history.Sales is { } sales)
        {
            var chart = AnalyticsCharts.Sales(sales, range, SalesMode.Selected, Compare);
            SalesChart = chart.Model;
            SalesSummary = AppLocalizer.Instance.Format(
                "Analytics.Chart.SalesSummary",
                AnalyticsFormat.Usd(chart.Totals.NetUsd),
                AnalyticsFormat.Count(chart.Totals.Units),
                AnalyticsFormat.Usd(chart.Totals.NetUsd / chart.Days));
            SalesCaption = AppLocalizer.Instance.Format("Analytics.Chart.DaysPacific", chart.Days) +
                           (sales.Pending.Count > 0 ? " · " + T("Analytics.Chart.SalesPending") : "");

            var earnings = AnalyticsEarnings.Build(sales, range, _rates);
            EarningsTiles = earnings.Tiles;
            EarningsRows = earnings.Rows;
            EarningsCaption = AppLocalizer.Instance.Format("Analytics.Earnings.Caption", earnings.RangeDays);
            EarningsNote = T("Analytics.Earnings.Note") +
                           (sales.UpgradingDays > 0 ? " " + AppLocalizer.Instance.Format("Analytics.Earnings.Upgrading", sales.UpgradingDays) : "");
        }
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private void OpenSteamDb() => ExternalLinks.Open(ExternalLinks.SteamDbCharts(Card.AppId));

    [RelayCommand]
    private void OpenStore() => ExternalLinks.Open(ExternalLinks.SteamStore(Card.AppId));

    private static string T(string key) => AppLocalizer.Instance.Get(key);
}
