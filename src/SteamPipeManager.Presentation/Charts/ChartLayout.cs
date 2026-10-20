using System.Globalization;

namespace SteamPipeManager.Presentation.Charts;

public readonly record struct ChartPoint(double X, double Y);

/// <param name="Segments">Veri olmayan dilimlerde kesilen parçalar; tek noktalı parça nokta olarak çizilir.</param>
/// <param name="Fill">Altı <see cref="ChartLayout.FillBaseline"/>'a kadar dolu.</param>
public sealed record ChartLine(ChartTone Tone, bool Faint, IReadOnlyList<IReadOnlyList<ChartPoint>> Segments, bool Fill);

public sealed record ChartBar(ChartTone Tone, bool Faint, double X, double Y, double Width, double Height);

public sealed record ChartTick(double Position, string Label);

/// <param name="Y">Çizgi serilerinde noktanın yeri; çubukta ve değer yoksa null.</param>
public sealed record ChartHoverRow(ChartTone Tone, bool Faint, string Text, double? Y, bool IsDetail = false);

/// <summary>İmlecin üstünde durduğu dilim: dikey çizginin yeri, başlık ve her serinin değeri.</summary>
public sealed record ChartHover(DateTime Time, double X, string Title, IReadOnlyList<ChartHoverRow> Rows);

/// <summary>
/// Bir grafiğin piksel yerleşimi: çizim alanı, eksen işaretleri, çizgiler ve çubuklar.
/// Yazı genişliğini çizici ölçüyor (<c>measure</c>); geri kalan her şey burada, testli.
/// </summary>
public sealed class ChartLayout
{
    public const double FontSize = 11;

    private const double TopPadding = 8;
    private const double RightPadding = 8;
    private const double LabelGap = 8;

    /// <summary>Alt eksen yazıları arasında en az bu kadar yer.</summary>
    private const double MinTickSpacing = 72;

    private readonly ChartModel _model;
    private readonly double _min;
    private readonly double _max;

    private ChartLayout(ChartModel model, double width, double height, double min, double max)
    {
        _model = model;
        _min = min;
        _max = max;
        Width = width;
        Height = height;
    }

    public double Width { get; }

    public double Height { get; }

    public double PlotLeft { get; private set; }

    public double PlotTop { get; private set; }

    public double PlotRight { get; private set; }

    public double PlotBottom { get; private set; }

    /// <summary>Sıfır çizgisi; eksen sıfırı içermiyorsa null.</summary>
    public double? ZeroY { get; private set; }

    /// <summary>Dolu çizgilerin alt sınırı: sıfır ya da (sıfır eksende değilse) alt kenar.</summary>
    public double FillBaseline => ZeroY ?? PlotBottom;

    public IReadOnlyList<ChartTick> XTicks { get; private set; } = [];

    public IReadOnlyList<ChartTick> YTicks { get; private set; } = [];

    public IReadOnlyList<ChartLine> Lines { get; private set; } = [];

    public IReadOnlyList<ChartBar> Bars { get; private set; } = [];

    public static ChartLayout Compute(ChartModel model, double width, double height, Func<string, double> measure)
    {
        var values = model.Series.SelectMany(s => s.Buckets).Select(b => b.Value).OfType<double>().ToList();
        var bars = model.Series.Any(s => s.Style == ChartStyle.Bars);

        var (min, max) = values.Count == 0 ? (0d, 1d) : (values.Min(), values.Max());

        // Çubuklar sıfırdan yükselir; sıfır her zaman eksende.
        if (model.FromZero || bars)
        {
            min = Math.Min(min, 0);
            max = Math.Max(max, 0);
        }

        var (ticks, low, high) = NiceTicks(min, max);
        var layout = new ChartLayout(model, width, height, low, high);

        var labels = ticks.Select(t => model.FormatAxis(t)).ToList();

        if (labels.Distinct().Count() < labels.Count)
        {
            labels = [.. ticks.Select(t => model.FormatAxis(t, exact: true))];
        }
        var labelWidth = labels.Count == 0 ? 0 : labels.Max(measure);

        layout.PlotLeft = Math.Ceiling(labelWidth + LabelGap);
        layout.PlotTop = TopPadding;
        layout.PlotRight = Math.Max(layout.PlotLeft + 1, width - RightPadding);
        layout.PlotBottom = Math.Max(layout.PlotTop + 1, height - FontSize - 2 * LabelGap);

        layout.YTicks = [.. ticks.Select((t, i) => new ChartTick(layout.YOf(t), labels[i]))];
        layout.ZeroY = low <= 0 && high >= 0 ? layout.YOf(0) : null;
        layout.XTicks = layout.TimeTicks(measure);
        layout.Lines = [.. model.Series.Where(s => s.Style != ChartStyle.Bars).Select(layout.LineOf)];
        layout.Bars = [.. model.Series.Where(s => s.Style == ChartStyle.Bars).SelectMany(layout.BarsOf)];

        return layout;
    }

    public double XOf(DateTime time) =>
        PlotLeft + (time - _model.From).TotalSeconds / Math.Max(1, (_model.To - _model.From).TotalSeconds) * (PlotRight - PlotLeft);

    public double YOf(double value) =>
        PlotBottom - (value - _min) / (_max - _min) * (PlotBottom - PlotTop);

    public DateTime TimeOf(double x) =>
        _model.From + TimeSpan.FromSeconds((x - PlotLeft) / Math.Max(1, PlotRight - PlotLeft) * (_model.To - _model.From).TotalSeconds);

    /// <summary>İmlecin x konumundaki dilim; çizim alanının dışındaysa null.</summary>
    public ChartHover? HoverAt(double x) =>
        x < PlotLeft || x > PlotRight ? null : HoverAtTime(TimeOf(x));

    /// <summary>
    /// Verilen zamanı içeren dilim. Grafikler imleci paylaşıyor: birinin üstündeki zaman
    /// ötekilerde de işaretleniyor; dilim uzunlukları farklı olsa da aynı ana denk gelen dilim.
    /// </summary>
    public ChartHover? HoverAtTime(DateTime time)
    {
        if (time < _model.From || time > _model.To)
        {
            return null;
        }

        // Ana seri (soluk olmayan ilk seri) dilimi belirliyor; ötekiler aynı sıradaki dilim.
        var primary = _model.Series.FirstOrDefault(s => !s.Faint) ?? _model.Series.FirstOrDefault();

        if (primary is null || primary.Buckets.Count == 0)
        {
            return null;
        }

        var index = IndexAt(primary.Buckets, time);
        var bucket = primary.Buckets[index];
        var rows = new List<ChartHoverRow>();

        foreach (var series in _model.Series)
        {
            if (index >= series.Buckets.Count)
            {
                continue;
            }

            var value = series.Buckets[index].Value;
            var text = $"{series.Name}: {(value is { } v ? _model.FormatValue(v) : "—")}";
            var y = value is { } point && series.Style != ChartStyle.Bars ? YOf(point) : (double?)null;

            rows.Add(new ChartHoverRow(series.Tone, series.Faint, text, y));

            if (series.Buckets[index].Detail is { Length: > 0 } detail)
            {
                rows.Add(new ChartHoverRow(series.Tone, series.Faint, detail, null, IsDetail: true));
            }
        }

        return new ChartHover(bucket.Start, XOf(bucket.Middle), _model.FormatBucket(bucket), rows);
    }

    private static int IndexAt(IReadOnlyList<ChartBucket> buckets, DateTime time)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            if (time >= buckets[i].Start && time < buckets[i].End)
            {
                return i;
            }
        }

        // Dilimlerin arasında ya da kenarında: en yakın orta nokta.
        var best = 0;

        for (var i = 1; i < buckets.Count; i++)
        {
            if (Math.Abs((buckets[i].Middle - time).Ticks) < Math.Abs((buckets[best].Middle - time).Ticks))
            {
                best = i;
            }
        }

        return best;
    }

    private ChartLine LineOf(ChartSeries series)
    {
        var segments = new List<IReadOnlyList<ChartPoint>>();
        var current = new List<ChartPoint>();

        foreach (var bucket in series.Buckets)
        {
            if (bucket.Value is { } value)
            {
                current.Add(new ChartPoint(XOf(bucket.Middle), YOf(value)));
            }
            else if (current.Count > 0)
            {
                segments.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        return new ChartLine(series.Tone, series.Faint, segments, series.Style == ChartStyle.Area);
    }

    private IEnumerable<ChartBar> BarsOf(ChartSeries series)
    {
        var zero = ZeroY ?? PlotBottom;

        foreach (var bucket in series.Buckets)
        {
            if (bucket.Value is not { } value || value == 0)
            {
                continue;
            }

            var left = XOf(bucket.Start);
            var right = XOf(bucket.End);
            var gap = right - left >= 4 ? Math.Min(3, (right - left) * 0.2) : 0;
            var y = YOf(value);

            yield return new ChartBar(
                series.Tone,
                series.Faint,
                left + gap / 2,
                Math.Min(y, zero),
                Math.Max(1, right - left - gap),
                Math.Max(1, Math.Abs(zero - y)));
        }
    }

    /// <summary>
    /// "Yuvarlak" eksen değerleri (1, 2, 2,5, 5 × 10ⁿ adımlarla) ve onlara genişletilmiş
    /// sınırlar. Hepsi aynı değerse ya da hiç değer yoksa yine okunur bir aralık.
    /// </summary>
    internal static (IReadOnlyList<double> Ticks, double Low, double High) NiceTicks(double min, double max)
    {
        if (max - min < 1e-9)
        {
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.1;
            min = min >= 0 && min - pad < 0 ? 0 : min - pad;
            max += pad;
        }

        var rough = (max - min) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= rough);

        // Adet eksende kesirli işaret olmasın.
        step = Math.Max(step, 1);

        var low = Math.Floor(min / step) * step;
        var high = Math.Ceiling(max / step) * step;
        var ticks = new List<double>();

        for (var t = low; t <= high + step / 2; t += step)
        {
            ticks.Add(Math.Round(t, 6));
        }

        return (ticks, low, high);
    }

    private static readonly TimeSpan[] TickSteps =
    [
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7), TimeSpan.FromDays(14),
        TimeSpan.FromDays(30), TimeSpan.FromDays(91), TimeSpan.FromDays(182), TimeSpan.FromDays(365),
    ];

    /// <summary>
    /// Alt eksen: yazılar sığacak en sık adım. Saatler gece yarısından, haftalar
    /// pazartesiden, aylar ayın birinden, yıllar ocaktan sayılıyor.
    /// </summary>
    private IReadOnlyList<ChartTick> TimeTicks(Func<string, double> measure)
    {
        var span = _model.To - _model.From;
        var fits = Math.Max(1, (PlotRight - PlotLeft) / MinTickSpacing);
        var step = TickSteps.FirstOrDefault(s => span / s <= fits, TickSteps[^1]);
        var culture = CultureInfo.CurrentCulture;
        var crossesYear = _model.From.Year != _model.To.Year;

        string Label(DateTime t) => step switch
        {
            _ when step < TimeSpan.FromDays(1) => t.TimeOfDay == TimeSpan.Zero ? t.ToString("d MMM", culture) : t.ToString("HH:mm", culture),
            _ when step < TimeSpan.FromDays(30) => t.ToString("d MMM", culture),
            _ when step < TimeSpan.FromDays(365) => t.ToString(crossesYear ? "MMM yy" : "MMM", culture),
            _ => t.ToString("yyyy", culture),
        };

        // Günlük dilimlerde tarih o günün ortasına (çubuğun altına) yazılıyor, iki çubuğun arasına değil.
        var middle = _model.TimeUnit == ChartTimeUnit.Days && step >= TimeSpan.FromDays(1) && step < TimeSpan.FromDays(30)
            ? TimeSpan.FromHours(12)
            : TimeSpan.Zero;

        var ticks = new List<ChartTick>();
        var lastRight = double.NegativeInfinity;

        foreach (var time in TickTimes(_model.From, _model.To, step))
        {
            var label = Label(time);
            var x = XOf(time + middle);
            var half = measure(label) / 2;

            // Kenardan taşan ya da öncekine binen yazı atlanıyor.
            if (x - half < PlotLeft - LabelGap || x + half > Width || x - half < lastRight + 4)
            {
                continue;
            }

            ticks.Add(new ChartTick(x, label));
            lastRight = x + half;
        }

        return ticks;
    }

    private static IEnumerable<DateTime> TickTimes(DateTime from, DateTime to, TimeSpan step)
    {
        DateTime first;
        Func<DateTime, DateTime> next;

        if (step < TimeSpan.FromDays(1))
        {
            var hours = (int)step.TotalHours;
            first = from.Date.AddHours(Math.Ceiling((from - from.Date).TotalHours / hours) * hours);
            next = t => t.AddHours(hours);
        }
        else if (step < TimeSpan.FromDays(7))
        {
            var days = (int)step.TotalDays;
            first = from.TimeOfDay == TimeSpan.Zero ? from.Date : from.Date.AddDays(1);
            next = t => t.AddDays(days);
        }
        else if (step < TimeSpan.FromDays(30))
        {
            var days = (int)step.TotalDays;
            var monday = from.Date.AddDays(-(((int)from.DayOfWeek + 6) % 7));
            first = monday < from ? monday.AddDays(7) : monday;
            next = t => t.AddDays(days);
        }
        else if (step < TimeSpan.FromDays(365))
        {
            var months = (int)Math.Round(step.TotalDays / 30.4);
            var month = new DateTime(from.Year, from.Month, 1);
            first = month < from ? month.AddMonths(1) : month;
            next = t => t.AddMonths(months);
        }
        else
        {
            var year = new DateTime(from.Year, 1, 1);
            first = year < from ? year.AddYears(1) : year;
            next = t => t.AddYears(1);
        }

        for (var t = first; t <= to; t = next(t))
        {
            yield return t;
        }
    }
}
