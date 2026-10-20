using System.Globalization;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Presentation.Charts;

/// <summary>Kazanç kartında bir dönem: net, yerel para karşılığı ve tahmini ödeme.</summary>
public sealed record EarningsTile(string Label, string Net, string Local, string Payout);

/// <summary>Brütten tahmini ödemeye bir satır; <paramref name="Emphasis"/> ara toplamlar.</summary>
public sealed record EarningsRow(string Label, string Value, string Local = "", bool Emphasis = false);

/// <param name="Projection">Ayın tamamlanmış günlerinin ortalamasıyla ay sonu; ayın ilk iki gününde null.</param>
public sealed record EarningsSummary(
    SalesTotals Today,
    SalesTotals Yesterday,
    SalesTotals Month,
    SalesTotals? Projection,
    SalesTotals Range,
    int RangeDays,
    IReadOnlyList<EarningsTile> Tiles,
    IReadOnlyList<EarningsRow> Rows);

/// <summary>
/// Satış geçmişinden kazanç: günler Pasifik saatine göre (Steam'in verdiği gibi). Yerel
/// para karşılıkları her günün kendi kuruyla; ay sonu tahmini en son kurla.
/// </summary>
public static class AnalyticsEarnings
{
    /// <summary>Ay sonu tahmini için en az bu kadar tamamlanmış gün.</summary>
    public const int MinDaysForProjection = 3;

    public static EarningsSummary Build(SalesHistory history, AnalyticsRange range, ExchangeRates? rates)
    {
        var known = history.Days.ToDictionary(d => d.Date, d => d.Totals);
        var today = history.Today;

        SalesTotals On(DateOnly date) => known.GetValueOrDefault(date, SalesTotals.Zero);

        IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                yield return date;
            }
        }

        SalesTotals Sum(IEnumerable<DateOnly> days) => days.Aggregate(SalesTotals.Zero, (sum, d) => sum.Add(On(d)));

        decimal? Local(IEnumerable<DateOnly> days, Func<SalesTotals, decimal> pick) =>
            rates?.Convert(days.Select(d => (d, pick(On(d)))));

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var month = Sum(Days(monthStart, today));

        // Bugün henüz bitmedi: tahmin tamamlanmış günlerden.
        var complete = today.Day - 1;
        SalesTotals? projection = null;

        if (complete >= MinDaysForProjection)
        {
            var sofar = Sum(Days(monthStart, today.AddDays(-1)));
            var factor = (decimal)DateTime.DaysInMonth(today.Year, today.Month) / complete;
            projection = new SalesTotals(
                (int)Math.Round(sofar.Units * factor), sofar.NetUsd * factor, sofar.GrossUsd * factor,
                sofar.ReturnsUsd * factor, sofar.TaxUsd * factor, sofar.ShareUsd * factor);
        }

        var first = history.Days.Count > 0 ? history.Days[0].Date : today;
        var rangeDays = Math.Max(AnalyticsCharts.DaysOf(range) is > 0 and var d ? d : today.DayNumber - first.DayNumber + 1, AnalyticsCharts.MinDays);
        var rangeFrom = today.AddDays(1 - rangeDays);
        var rangeTotals = Sum(Days(rangeFrom, today));
        var currency = rates?.Currency;

        EarningsTile Tile(string key, SalesTotals totals, decimal? local) => new(
            T(key),
            AnalyticsFormat.Usd(totals.NetUsd),
            AnalyticsFormat.Local(local, currency),
            AppLocalizer.Instance.Format("Analytics.Earnings.Payout", AnalyticsFormat.Usd(totals.ShareUsd)));

        var tiles = new List<EarningsTile>
        {
            Tile("Analytics.Sales.Today", On(today), Local([today], t => t.NetUsd)),
            Tile("Analytics.Sales.Yesterday", On(today.AddDays(-1)), Local([today.AddDays(-1)], t => t.NetUsd)),
            Tile("Analytics.Earnings.Month", month, Local(Days(monthStart, today), t => t.NetUsd)),
            projection is { } p
                ? Tile("Analytics.Earnings.Projection", p, rates?.Latest is { } latest ? p.NetUsd * latest : null)
                : new EarningsTile(T("Analytics.Earnings.Projection"), "—", "", ""),
        };

        var returnRate = rangeTotals.GrossUsd > 0 ? rangeTotals.ReturnsUsd / rangeTotals.GrossUsd : 0;

        var rows = new List<EarningsRow>
        {
            new(T("Analytics.Earnings.Gross"), AnalyticsFormat.Usd(rangeTotals.GrossUsd)),
            new(AppLocalizer.Instance.Format("Analytics.Earnings.Returns", returnRate.ToString("P1", CultureInfo.CurrentCulture)),
                AnalyticsFormat.Usd(-rangeTotals.ReturnsUsd)),
            new(T("Analytics.Earnings.Tax"), AnalyticsFormat.Usd(-rangeTotals.TaxUsd)),
            new(T("Analytics.Earnings.Net"), AnalyticsFormat.Usd(rangeTotals.NetUsd),
                AnalyticsFormat.Local(Local(Days(rangeFrom, today), t => t.NetUsd), currency), Emphasis: true),
            new(T("Analytics.Earnings.SteamCut"), AnalyticsFormat.Usd(-rangeTotals.SteamCutUsd)),
            new(T("Analytics.Earnings.Share"), AnalyticsFormat.Usd(rangeTotals.ShareUsd),
                AnalyticsFormat.Local(Local(Days(rangeFrom, today), t => t.ShareUsd), currency), Emphasis: true),
        };

        return new EarningsSummary(On(today), On(today.AddDays(-1)), month, projection, rangeTotals, rangeDays, tiles, rows);
    }

    private static string T(string key) => AppLocalizer.Instance.Get(key);
}
