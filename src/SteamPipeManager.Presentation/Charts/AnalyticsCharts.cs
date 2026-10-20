using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Presentation.Charts;

public enum AnalyticsRange
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    All,
}

public enum WishlistChartMode
{
    Total,
    Daily,
}

public enum SalesChartMode
{
    /// <summary>Net gelir (iade ve vergi düşülmüş, Steam payı düşülmemiş).</summary>
    Revenue,

    /// <summary>Tahmini ödeme: net × gelir payı.</summary>
    Payout,

    Units,
}

/// <param name="Peak">Aralığın en yükseği; veri yoksa null.</param>
/// <param name="HasGaps">Aralıkta veri olmayan dilim var: uygulama ve widget kapalıydı.</param>
public sealed record PlayersChart(ChartModel Model, int? Peak, int? Average, bool HasGaps);

/// <param name="Net">Aralıktaki net değişim (eklenen − silinen − satın alınan − hediye).</param>
/// <param name="Days">Gösterilen gün sayısı; 24 saatlik aralıkta da en az bir hafta.</param>
public sealed record WishlistChart(ChartModel Model, int Net, int Days);

public sealed record SalesChart(ChartModel Model, SalesTotals Totals, int Days);

/// <summary>
/// Önbellekteki geçmişi seçilen aralığın grafiklerine çevirir. Zamanlar yerel saate
/// çevriliyor; günlük seriler (wishlist GMT, satış Pasifik) o günün takvim tarihine
/// oturtuluyor, böylece üç grafik aynı tarihte hizalı.
/// </summary>
public static class AnalyticsCharts
{
    /// <summary>Günlük grafiklerde en az bu kadar gün; tek çubuk bir şey anlatmıyor.</summary>
    public const int MinDays = 7;

    /// <summary>Bundan uzun günlük seriler haftalık toplanıyor.</summary>
    private const int MaxDailyBuckets = 400;

    public static int DaysOf(AnalyticsRange range) => range switch
    {
        AnalyticsRange.Day or AnalyticsRange.Week => 7,
        AnalyticsRange.Month => 30,
        AnalyticsRange.Quarter => 90,
        AnalyticsRange.Year => 365,
        _ => 0,
    };

    private static TimeSpan SpanOf(AnalyticsRange range) => range switch
    {
        AnalyticsRange.Day => TimeSpan.FromDays(1),
        AnalyticsRange.Week => TimeSpan.FromDays(7),
        AnalyticsRange.Month => TimeSpan.FromDays(30),
        AnalyticsRange.Quarter => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(365),
    };

    private static TimeSpan BucketFor(TimeSpan span) => span.TotalDays switch
    {
        <= 2 => TimeSpan.FromMinutes(15),
        <= 10 => TimeSpan.FromHours(1),
        <= 40 => TimeSpan.FromHours(4),
        <= 120 => TimeSpan.FromHours(12),
        <= MaxDailyBuckets => TimeSpan.FromDays(1),
        _ => TimeSpan.FromDays(7),
    };

    private static ChartTimeUnit UnitOf(TimeSpan bucket) => bucket.TotalHours switch
    {
        < 1 => ChartTimeUnit.Minutes,
        < 24 => ChartTimeUnit.Hours,
        < 24 * 7 => ChartTimeUnit.Days,
        _ => ChartTimeUnit.Weeks,
    };

    private static string T(string key) => AppLocalizer.Instance.Get(key);

    // ------------------------------------------------------------------ oyuncular

    public static PlayersChart Players(AppHistory history, AnalyticsRange range, bool compare)
    {
        var now = history.Now.LocalDateTime;
        var earliest = EarliestPlayers(history);

        var span = range == AnalyticsRange.All && earliest is { } first
            ? TimeSpan.FromTicks(Math.Max((now - first).Ticks, TimeSpan.FromDays(1).Ticks))
            : SpanOf(range);

        var bucket = BucketFor(span);
        var from = Floor(now - span, bucket);
        var current = PlayerBuckets(history, from, now, bucket);

        var series = new List<ChartSeries>
        {
            new(T("Analytics.Chart.Peak"), ChartStyle.Area, ChartTone.Players, [.. current.Select(b => b.Peak)]),
        };

        // Kısa dilimde ortalama zirveyle aynı; önceki dönem açıksa grafik kalabalıklaşmasın.
        if (compare && range != AnalyticsRange.All)
        {
            var previous = PlayerBuckets(history, from - span, now - span, bucket);
            series.Add(new(T("Analytics.Chart.Previous"), ChartStyle.Line, ChartTone.Players, Shift(previous.Select(b => b.Peak), span), Faint: true));
        }
        else if (bucket >= TimeSpan.FromHours(1))
        {
            series.Add(new(T("Analytics.Chart.Average"), ChartStyle.Line, ChartTone.Players, [.. current.Select(b => b.Average)], Faint: true));
        }

        var peaks = current.Select(b => b.Peak.Value).OfType<double>().ToList();
        var weight = current.Sum(b => b.Count);
        var average = weight == 0 ? (int?)null : (int)Math.Round(current.Sum(b => (b.Average.Value ?? 0) * b.Count) / weight);

        // Kesik: ilk veriden sonra boş dilim. İlk veriden önceki boşluk takip başlamadan önce.
        var firstData = current.FindIndex(b => b.Peak.Value is not null);
        var gaps = firstData >= 0 && current.Skip(firstData).Any(b => b.Peak.Value is null);

        // Sağ kenar son dilimin sonu: içinde bulunulan dilim de tam genişlikte çizilsin.
        var model = new ChartModel(from, from + bucket * current.Count, series, ChartValueKind.Count, UnitOf(bucket));
        return new PlayersChart(model, peaks.Count > 0 ? (int)peaks.Max() : null, average, gaps);
    }

    private static DateTime? EarliestPlayers(AppHistory history)
    {
        var hour = history.PlayerHours.Count > 0 ? history.PlayerHours[0].At.LocalDateTime : (DateTime?)null;
        var sample = history.PlayerSamples.Count > 0 ? history.PlayerSamples[0].At.LocalDateTime : (DateTime?)null;
        return hour ?? sample;
    }

    private sealed record PlayerBucket(ChartBucket Peak, ChartBucket Average, int Count);

    /// <summary>
    /// Dilim başına en yüksek ve ortalama. Saatlik özetler örnek sayılarıyla ağırlıklı;
    /// son 30 günün örnekleri tek tek.
    /// </summary>
    private static List<PlayerBucket> PlayerBuckets(AppHistory history, DateTime from, DateTime to, TimeSpan size)
    {
        // Son dilim "şimdi"yi içeriyor; tam sınıra düşen son örnek de bir dilimde.
        var count = (int)Math.Floor((to - from) / size) + 1;
        var max = new int?[count];
        var sum = new double[count];
        var n = new int[count];

        void Add(DateTime at, int peak, int average, int weight)
        {
            var i = (int)((at - from) / size);

            if (i < 0 || i >= count)
            {
                return;
            }

            max[i] = Math.Max(max[i] ?? 0, peak);
            sum[i] += (double)average * weight;
            n[i] += weight;
        }

        foreach (var hour in history.PlayerHours)
        {
            Add(hour.At.LocalDateTime, hour.Max, hour.Avg, hour.N);
        }

        foreach (var sample in history.PlayerSamples)
        {
            Add(sample.At.LocalDateTime, sample.C, sample.C, 1);
        }

        var result = new List<PlayerBucket>(count);

        for (var i = 0; i < count; i++)
        {
            var start = from + size * i;
            var end = start + size;
            var average = n[i] == 0 ? (double?)null : Math.Round(sum[i] / n[i]);

            result.Add(new PlayerBucket(new(start, end, max[i]), new(start, end, average), n[i]));
        }

        return result;
    }

    // ------------------------------------------------------------------ wishlist

    public static WishlistChart Wishlist(WishlistHistory history, AnalyticsRange range, WishlistChartMode mode, bool compare)
    {
        var known = history.Days.ToDictionary(d => d.Date, d => d.Counts);
        var first = history.Since ?? (history.Days.Count > 0 ? history.Days[0].Date : history.Today);
        var days = Math.Max(DaysOf(range) is > 0 and var d ? d : history.Today.DayNumber - first.DayNumber + 1, MinDays);
        var start = history.Today.AddDays(1 - days);

        // Gün sonundaki toplam: bugünkü toplamdan sonraki günlerin neti geri alınarak.
        var totals = new Dictionary<DateOnly, int>();
        var running = history.Total;

        for (var date = history.Today; date >= start.AddDays(-days); date = date.AddDays(-1))
        {
            totals[date] = running;
            running -= known.TryGetValue(date, out var c) ? c.Net : 0;
        }

        double? Value(DateOnly date) =>
            !known.ContainsKey(date) && date < history.Today
                ? null
                : mode == WishlistChartMode.Daily ? (known.TryGetValue(date, out var c) ? c.Net : 0) : totals.GetValueOrDefault(date);

        string? Detail(DateOnly date) =>
            mode == WishlistChartMode.Daily && known.TryGetValue(date, out var c)
                ? AppLocalizer.Instance.Format("Analytics.Chart.WishlistDetail",
                    AnalyticsFormat.Count(c.Adds), AnalyticsFormat.Count(c.Deletes), AnalyticsFormat.Count(c.Purchases + c.Gifts))
                : null;

        var name = T(mode == WishlistChartMode.Daily ? "Analytics.Chart.NetChange" : "Analytics.Chart.Total");
        var style = mode == WishlistChartMode.Daily ? ChartStyle.Bars : ChartStyle.Area;
        var weekly = days > MaxDailyBuckets;

        // Haftalıkta günlük farklar toplanıyor, toplam ise haftanın son günündeki değer.
        var buckets = DailyBuckets(start, days, Value, Detail, weekly, sum: mode == WishlistChartMode.Daily);
        var series = new List<ChartSeries> { new(name, style, ChartTone.Wishlist, buckets) };

        if (compare && range != AnalyticsRange.All)
        {
            var previous = DailyBuckets(start.AddDays(-days), days, Value, null, weekly, sum: mode == WishlistChartMode.Daily);
            series.Add(new(T("Analytics.Chart.Previous"), ChartStyle.Line, ChartTone.Wishlist, Shift(previous, TimeSpan.FromDays(days)), Faint: true));
        }

        var net = Enumerable.Range(0, days).Sum(i => known.TryGetValue(start.AddDays(i), out var c) ? c.Net : 0);
        var model = new ChartModel(Local(start), Local(history.Today.AddDays(1)), series, ChartValueKind.Count, weekly ? ChartTimeUnit.Weeks : ChartTimeUnit.Days)
        {
            FromZero = mode == WishlistChartMode.Daily,
        };

        return new WishlistChart(model, net, days);
    }

    // ------------------------------------------------------------------ satış

    public static SalesChart Sales(SalesHistory history, AnalyticsRange range, SalesChartMode mode, bool compare)
    {
        var known = history.Days.ToDictionary(d => d.Date, d => d.Totals);
        var first = history.Days.Count > 0 ? history.Days[0].Date : history.Today;
        var days = Math.Max(DaysOf(range) is > 0 and var d ? d : history.Today.DayNumber - first.DayNumber + 1, MinDays);
        var start = history.Today.AddDays(1 - days);

        // Listede olmayan gün satışsız; henüz inmemiş gün bilinmiyor.
        SalesTotals? TotalsOn(DateOnly date) =>
            history.Pending.Contains(date) ? null : known.GetValueOrDefault(date, SalesTotals.Zero);

        double? Value(DateOnly date) => TotalsOn(date) is { } t
            ? mode switch
            {
                SalesChartMode.Units => t.Units,
                SalesChartMode.Payout => (double)t.ShareUsd,
                _ => (double)t.NetUsd,
            }
            : null;

        string? Detail(DateOnly date) => TotalsOn(date) is { Units: > 0 } t
            ? mode == SalesChartMode.Units
                ? AnalyticsFormat.Usd(t.NetUsd)
                : AppLocalizer.Instance.Format("Analytics.Chart.SalesDetail", AnalyticsFormat.Usd(t.GrossUsd), AnalyticsFormat.Count(t.Units))
            : null;

        var weekly = days > MaxDailyBuckets;
        var buckets = DailyBuckets(start, days, Value, Detail, weekly, sum: true);
        var kind = mode == SalesChartMode.Units ? ChartValueKind.Count : ChartValueKind.Usd;
        var series = new List<ChartSeries>
        {
            new(T(mode switch
            {
                SalesChartMode.Units => "Analytics.Chart.Units",
                SalesChartMode.Payout => "Analytics.Chart.Payout",
                _ => "Analytics.Chart.Net",
            }), ChartStyle.Bars, ChartTone.Sales, buckets),
        };

        if (compare && range != AnalyticsRange.All)
        {
            var previous = DailyBuckets(start.AddDays(-days), days, Value, null, weekly, sum: true);
            series.Add(new(T("Analytics.Chart.Previous"), ChartStyle.Line, ChartTone.Sales, Shift(previous, TimeSpan.FromDays(days)), Faint: true));
        }
        else if (!weekly && days > MinDays)
        {
            series.Add(new(T("Analytics.Chart.MovingAverage"), ChartStyle.Line, ChartTone.Sales, MovingAverage(buckets, 7), Faint: true));
        }

        var totals = Enumerable.Range(0, days)
            .Select(i => TotalsOn(start.AddDays(i)) ?? SalesTotals.Zero)
            .Aggregate(SalesTotals.Zero, (sum, t) => sum.Add(t));

        var model = new ChartModel(Local(start), Local(history.Today.AddDays(1)), series, kind, weekly ? ChartTimeUnit.Weeks : ChartTimeUnit.Days);
        return new SalesChart(model, totals, days);
    }

    // ------------------------------------------------------------------ ortak

    private static DateTime Local(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);

    /// <summary>
    /// Gün gün (ya da haftalık) dilimler. Haftalıkta <paramref name="sum"/> günleri topluyor,
    /// değilse haftanın son bilinen değerini alıyor (birikimli toplam için).
    /// </summary>
    private static List<ChartBucket> DailyBuckets(
        DateOnly start, int days, Func<DateOnly, double?> value, Func<DateOnly, string?>? detail, bool weekly, bool sum)
    {
        var size = weekly ? 7 : 1;
        var result = new List<ChartBucket>();

        for (var offset = 0; offset < days; offset += size)
        {
            var first = start.AddDays(offset);
            var length = Math.Min(size, days - offset);
            var values = Enumerable.Range(0, length).Select(i => value(first.AddDays(i))).ToList();
            var known = values.OfType<double>().ToList();

            double? combined = known.Count == 0 ? null : sum ? known.Sum() : values.LastOrDefault(v => v is not null);

            result.Add(new ChartBucket(Local(first), Local(first.AddDays(length)), combined, size == 1 ? detail?.Invoke(first) : null));
        }

        return result;
    }

    /// <summary>Önceki dönemin dilimleri şimdiki döneme kaydırılıyor; üst üste okunsun diye.</summary>
    private static List<ChartBucket> Shift(IEnumerable<ChartBucket> buckets, TimeSpan by) =>
        [.. buckets.Select(b => b with { Start = b.Start + by, End = b.End + by, Detail = null })];

    /// <summary>Son <paramref name="window"/> dilimin ortalaması; bilinmeyen dilimler sayılmıyor.</summary>
    private static List<ChartBucket> MovingAverage(IReadOnlyList<ChartBucket> buckets, int window)
    {
        var result = new List<ChartBucket>(buckets.Count);

        for (var i = 0; i < buckets.Count; i++)
        {
            var known = buckets.Skip(Math.Max(0, i - window + 1)).Take(Math.Min(window, i + 1)).Select(b => b.Value).OfType<double>().ToList();
            result.Add(buckets[i] with { Value = i + 1 < window || known.Count == 0 ? null : known.Average(), Detail = null });
        }

        return result;
    }

    /// <summary>Zamanı dilim sınırına indirir: gün ve üstü gece yarısına, hafta pazartesiye.</summary>
    private static DateTime Floor(DateTime time, TimeSpan size)
    {
        if (size >= TimeSpan.FromDays(7))
        {
            return time.Date.AddDays(-(((int)time.DayOfWeek + 6) % 7));
        }

        if (size >= TimeSpan.FromDays(1))
        {
            return time.Date;
        }

        var sinceMidnight = time - time.Date;
        return time.Date + size * Math.Floor(sinceMidnight / size);
    }
}
