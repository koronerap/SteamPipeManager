using System.Globalization;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Presentation.Charts;

/// <summary>Serinin rengi; çiziciler (WPF, Avalonia) tonu kendi fırçasına çeviriyor.</summary>
public enum ChartTone
{
    Players,
    Wishlist,
    Sales,
}

public enum ChartStyle
{
    Line,

    /// <summary>Çizgi ve altı yarı saydam dolu.</summary>
    Area,

    Bars,
}

/// <summary>Değerlerin biçimi: eksende kısa, ipucunda tam.</summary>
public enum ChartValueKind
{
    Count,
    Usd,
}

/// <summary>Bir dilimin uzunluğu; ipucu başlığı buna göre yazılıyor.</summary>
public enum ChartTimeUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
}

/// <summary>
/// Grafikte bir dilim: yerel saatle başlangıç ve bitiş, değer. Null "veri yok" demek;
/// çizgi orada kesiliyor, sıfır sayılmıyor.
/// </summary>
/// <param name="Detail">İpucunda değerin altına yazılan ek bilgi (ör. eklenen/silinen).</param>
public readonly record struct ChartBucket(DateTime Start, DateTime End, double? Value, string? Detail = null)
{
    public DateTime Middle => Start + (End - Start) / 2;
}

/// <param name="Faint">Soluk çizilir: önceki dönem, ortalama gibi yardımcı seriler.</param>
public sealed record ChartSeries(string Name, ChartStyle Style, ChartTone Tone, IReadOnlyList<ChartBucket> Buckets, bool Faint = false);

/// <summary>
/// Bir grafiğin tamamı, çizimden bağımsız. Yerleşimi <see cref="ChartLayout"/> hesaplıyor;
/// WPF ve Avalonia yalnızca onu çiziyor.
/// </summary>
public sealed record ChartModel(
    DateTime From,
    DateTime To,
    IReadOnlyList<ChartSeries> Series,
    ChartValueKind ValueKind,
    ChartTimeUnit TimeUnit)
{
    /// <summary>Eksen sıfırdan başlasın mı; birikimli toplamlarda başlamıyor, yoksa çizgi düz görünürdü.</summary>
    public bool FromZero { get; init; } = true;

    public bool IsEmpty => Series.All(s => s.Buckets.All(b => b.Value is null));

    public string FormatValue(double value) => ValueKind switch
    {
        ChartValueKind.Usd => AnalyticsFormat.Usd((decimal)value),
        _ => AnalyticsFormat.Count((int)Math.Round(value)),
    };

    /// <summary>
    /// Eksen yazısı: kısa biçim (36K, $1.2K). <paramref name="exact"/>: aralık dar olduğu için
    /// kısa biçimde yazılar aynı çıkıyorsa (36K, 36K, 36K) tam sayı.
    /// </summary>
    public string FormatAxis(double value, bool exact = false) => (ValueKind, exact) switch
    {
        (ChartValueKind.Usd, false) => AnalyticsFormat.CompactUsd((decimal)value),
        (ChartValueKind.Usd, true) => (value < 0 ? "−$" : "$") + Math.Abs(value).ToString("N0", CultureInfo.CurrentCulture),
        (_, true) => AnalyticsFormat.Signed((int)Math.Round(value)).TrimStart('+'),
        _ => value < 0 ? "−" + AnalyticsFormat.Compact((int)Math.Round(-value)) : AnalyticsFormat.Compact((int)Math.Round(value)),
    };

    /// <summary>İpucunun başlığı: dilimin zamanı, uzunluğuna göre.</summary>
    public string FormatBucket(ChartBucket bucket)
    {
        var culture = CultureInfo.CurrentCulture;

        return TimeUnit switch
        {
            ChartTimeUnit.Minutes => bucket.Start.ToString("d MMM HH:mm", culture),
            ChartTimeUnit.Hours => bucket.Start.ToString("d MMM HH:mm", culture) + "–" + bucket.End.ToString("HH:mm", culture),
            ChartTimeUnit.Weeks => bucket.Start.ToString("d MMM", culture) + " – " + bucket.End.AddDays(-1).ToString("d MMM yyyy", culture),
            _ => bucket.Start.ToString("ddd d MMM yyyy", culture),
        };
    }
}
