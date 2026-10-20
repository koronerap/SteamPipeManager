using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SteamPipeManager.Presentation.Charts;
using SteamPipeManager.Presentation.Localization;

namespace SteamPipeManager.App.Controls;

/// <summary>
/// Analitik grafiği. Ne çizileceğini <see cref="ChartLayout"/> hesaplıyor (Presentation,
/// testli); bu sınıf yalnızca çiziyor. Avalonia'daki eşi aynı yapıda.
/// İmlecin zamanı <see cref="HoverTime"/> ile paylaşılıyor: aynı sayfadaki grafikler aynı
/// anı işaretliyor.
/// </summary>
public sealed class ChartView : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(ChartModel), typeof(ChartView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverTimeProperty = DependencyProperty.Register(
        nameof(HoverTime), typeof(DateTime?), typeof(ChartView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ChartView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Regular = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush TooltipBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x24)));
    private static readonly Brush TooltipBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TooltipText = Brushes.White;

    private ChartLayout? _layout;
    private (ChartModel Model, double Width, double Height)? _layoutKey;

    public ChartModel? Model
    {
        get => (ChartModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public DateTime? HoverTime
    {
        get => (DateTime?)GetValue(HoverTimeProperty);
        set => SetValue(HoverTimeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static Color ColorOf(ChartTone tone) => tone switch
    {
        ChartTone.Wishlist => Color.FromRgb(0xE0, 0x6C, 0x9F),
        ChartTone.Sales => Color.FromRgb(0x3F, 0xB9, 0x50),
        _ => Color.FromRgb(0x4C, 0x9A, 0xFF),
    };

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Brush BrushOf(ChartTone tone, byte alpha)
    {
        var color = ColorOf(tone);
        return Frozen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
    }

    private Brush ForegroundWith(byte alpha) =>
        Foreground is SolidColorBrush { Color: var c }
            ? Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)))
            : Frozen(new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)));

    private FormattedText Text(string text, Brush brush, double size = ChartLayout.FontSize, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? Regular, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private ChartLayout? LayoutFor(ChartModel model)
    {
        if (_layoutKey is not { } key || key.Model != model || key.Width != ActualWidth || key.Height != ActualHeight)
        {
            _layout = ChartLayout.Compute(model, ActualWidth, ActualHeight, s => Text(s, Brushes.Black).WidthIncludingTrailingWhitespace);
            _layoutKey = (model, ActualWidth, ActualHeight);
        }

        return _layout;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Saydam zemin: imleç boş yerde de algılansın.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (Model is not { } model || ActualWidth < 60 || ActualHeight < 50 || LayoutFor(model) is not { } layout)
        {
            return;
        }

        var label = ForegroundWith(0x99);
        var grid = new Pen(ForegroundWith(0x1A), 1);
        grid.Freeze();

        foreach (var tick in layout.YTicks)
        {
            var y = Math.Round(tick.Position) + 0.5;
            dc.DrawLine(grid, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));

            var text = Text(tick.Label, label);
            dc.DrawText(text, new Point(layout.PlotLeft - 8 - text.WidthIncludingTrailingWhitespace, y - text.Height / 2));
        }

        foreach (var tick in layout.XTicks)
        {
            var text = Text(tick.Label, label);
            dc.DrawText(text, new Point(tick.Position - text.WidthIncludingTrailingWhitespace / 2, layout.PlotBottom + 6));
        }

        if (layout.ZeroY is { } zero && layout.YTicks.Count > 0 && layout.YTicks[0].Position != zero)
        {
            var zeroPen = new Pen(ForegroundWith(0x40), 1);
            zeroPen.Freeze();
            dc.DrawLine(zeroPen, new Point(layout.PlotLeft, Math.Round(zero) + 0.5), new Point(layout.PlotRight, Math.Round(zero) + 0.5));
        }

        foreach (var bar in layout.Bars)
        {
            dc.DrawRectangle(BrushOf(bar.Tone, bar.Faint ? (byte)0x55 : (byte)0xCC), null, new Rect(bar.X, bar.Y, bar.Width, bar.Height));
        }

        foreach (var line in layout.Lines)
        {
            DrawLine(dc, layout, line);
        }

        if (model.IsEmpty)
        {
            var empty = Text(AppLocalizer.Instance.Get("Analytics.Chart.NoData"), label, 12);
            dc.DrawText(empty, new Point(
                (layout.PlotLeft + layout.PlotRight - empty.WidthIncludingTrailingWhitespace) / 2,
                (layout.PlotTop + layout.PlotBottom - empty.Height) / 2));
        }

        if (HoverTime is { } time && layout.HoverAtTime(time) is { } hover)
        {
            DrawHover(dc, layout, hover);
        }
    }

    private static void DrawLine(DrawingContext dc, ChartLayout layout, ChartLine line)
    {
        var stroke = new Pen(BrushOf(line.Tone, line.Faint ? (byte)0x80 : (byte)0xFF), line.Faint ? 1.5 : 2)
        {
            LineJoin = PenLineJoin.Round,
            DashStyle = line.Faint ? new DashStyle([3, 2], 0) : DashStyles.Solid,
        };
        stroke.Freeze();

        var fill = BrushOf(line.Tone, 0x30);

        foreach (var segment in line.Segments)
        {
            if (segment.Count == 1)
            {
                dc.DrawEllipse(stroke.Brush, null, new Point(segment[0].X, segment[0].Y), 2.5, 2.5);
                continue;
            }

            var points = segment.Select(p => new Point(p.X, p.Y)).ToList();

            if (line.Fill)
            {
                var area = new StreamGeometry();

                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(points[0].X, layout.FillBaseline), isFilled: true, isClosed: true);
                    ctx.PolyLineTo(points, isStroked: false, isSmoothJoin: false);
                    ctx.LineTo(new Point(points[^1].X, layout.FillBaseline), isStroked: false, isSmoothJoin: false);
                }

                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            var path = new StreamGeometry();

            using (var ctx = path.Open())
            {
                ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
                ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
            }

            path.Freeze();
            dc.DrawGeometry(null, stroke, path);
        }
    }

    private void DrawHover(DrawingContext dc, ChartLayout layout, ChartHover hover)
    {
        var guide = new Pen(ForegroundWith(0x66), 1);
        guide.Freeze();
        dc.DrawLine(guide, new Point(Math.Round(hover.X) + 0.5, layout.PlotTop), new Point(Math.Round(hover.X) + 0.5, layout.PlotBottom));

        foreach (var row in hover.Rows)
        {
            if (row.Y is { } y)
            {
                dc.DrawEllipse(BrushOf(row.Tone, row.Faint ? (byte)0x99 : (byte)0xFF), new Pen(TooltipBackground, 1.5), new Point(hover.X, y), 4, 4);
            }
        }

        // İpucu kutusu: imlecin sağında, sığmazsa solunda.
        var title = Text(hover.Title, TooltipText, ChartLayout.FontSize + 1, SemiBold);
        var rows = hover.Rows
            .Select(r => (Row: r, Text: Text(r.Text, r.IsDetail ? Frozen(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF))) : TooltipText)))
            .ToList();

        const double padding = 8;
        const double dot = 12;
        var width = Math.Max(title.WidthIncludingTrailingWhitespace, rows.Count == 0 ? 0 : rows.Max(r => r.Text.WidthIncludingTrailingWhitespace + dot)) + 2 * padding;
        var height = title.Height + rows.Sum(r => r.Text.Height + 2) + 2 * padding + 2;

        var left = hover.X + 12 + width <= ActualWidth ? hover.X + 12 : Math.Max(0, hover.X - 12 - width);
        var top = layout.PlotTop;

        dc.DrawRoundedRectangle(TooltipBackground, new Pen(TooltipBorder, 1), new Rect(left, top, width, height), 6, 6);
        dc.DrawText(title, new Point(left + padding, top + padding));

        var rowTop = top + padding + title.Height + 4;

        foreach (var (row, text) in rows)
        {
            if (!row.IsDetail)
            {
                dc.DrawEllipse(BrushOf(row.Tone, row.Faint ? (byte)0x99 : (byte)0xFF), null, new Point(left + padding + 4, rowTop + text.Height / 2), 4, 4);
            }

            dc.DrawText(text, new Point(left + padding + dot, rowTop));
            rowTop += text.Height + 2;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetCurrentValue(HoverTimeProperty, _layout?.HoverAt(e.GetPosition(this).X)?.Time);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoverTimeProperty, null);
    }
}
