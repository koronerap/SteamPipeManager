using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SteamPipeManager.Presentation.Charts;
using SteamPipeManager.Presentation.Localization;

namespace SteamPipeManager.Desktop.Controls;

/// <summary>
/// Analitik grafiği. Ne çizileceğini <see cref="ChartLayout"/> hesaplıyor (Presentation,
/// testli); bu sınıf yalnızca çiziyor. WPF'teki eşi aynı yapıda.
/// İmlecin zamanı <see cref="HoverTime"/> ile paylaşılıyor: aynı sayfadaki grafikler aynı
/// anı işaretliyor.
/// </summary>
public sealed class ChartView : Control
{
    public static readonly StyledProperty<ChartModel?> ModelProperty =
        AvaloniaProperty.Register<ChartView, ChartModel?>(nameof(Model));

    public static readonly StyledProperty<DateTime?> HoverTimeProperty =
        AvaloniaProperty.Register<ChartView, DateTime?>(nameof(HoverTime), defaultBindingMode: BindingMode.TwoWay);

    private static readonly IBrush TooltipBackground = new ImmutableSolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x24));
    private static readonly IBrush TooltipBorder = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush TooltipText = Brushes.White;
    private static readonly IBrush TooltipDetail = new ImmutableSolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    private ChartLayout? _layout;
    private (ChartModel Model, double Width, double Height)? _layoutKey;

    static ChartView()
    {
        AffectsRender<ChartView>(ModelProperty, HoverTimeProperty, TextElement.ForegroundProperty);
    }

    public ChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public DateTime? HoverTime
    {
        get => GetValue(HoverTimeProperty);
        set => SetValue(HoverTimeProperty, value);
    }

    private static Color ColorOf(ChartTone tone) => tone switch
    {
        ChartTone.Wishlist => Color.FromRgb(0xE0, 0x6C, 0x9F),
        ChartTone.Sales => Color.FromRgb(0x3F, 0xB9, 0x50),
        _ => Color.FromRgb(0x4C, 0x9A, 0xFF),
    };

    private static IBrush BrushOf(ChartTone tone, byte alpha)
    {
        var color = ColorOf(tone);
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private IBrush ForegroundWith(byte alpha)
    {
        var color = GetValue(TextElement.ForegroundProperty) is ISolidColorBrush { Color: var c } ? c : Colors.White;
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private FormattedText Text(string text, IBrush brush, double size = ChartLayout.FontSize, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight), size, brush);

    private ChartLayout LayoutFor(ChartModel model)
    {
        var (width, height) = (Bounds.Width, Bounds.Height);

        if (_layout is null || _layoutKey is not { } key || key.Model != model || key.Width != width || key.Height != height)
        {
            _layout = ChartLayout.Compute(model, width, height, s => Text(s, Brushes.Black).WidthIncludingTrailingWhitespace);
            _layoutKey = (model, width, height);
        }

        return _layout;
    }

    public override void Render(DrawingContext context)
    {
        // Saydam zemin: imleç boş yerde de algılansın.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Model is not { } model || Bounds.Width < 60 || Bounds.Height < 50)
        {
            return;
        }

        var layout = LayoutFor(model);
        var label = ForegroundWith(0x99);
        var grid = new Pen(ForegroundWith(0x1A), 1);

        foreach (var tick in layout.YTicks)
        {
            var y = Math.Round(tick.Position) + 0.5;
            context.DrawLine(grid, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));

            var text = Text(tick.Label, label);
            context.DrawText(text, new Point(layout.PlotLeft - 8 - text.WidthIncludingTrailingWhitespace, y - text.Height / 2));
        }

        foreach (var tick in layout.XTicks)
        {
            var text = Text(tick.Label, label);
            context.DrawText(text, new Point(tick.Position - text.WidthIncludingTrailingWhitespace / 2, layout.PlotBottom + 6));
        }

        if (layout.ZeroY is { } zero && layout.YTicks.Count > 0 && layout.YTicks[0].Position != zero)
        {
            var y = Math.Round(zero) + 0.5;
            context.DrawLine(new Pen(ForegroundWith(0x40), 1), new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));
        }

        foreach (var bar in layout.Bars)
        {
            context.FillRectangle(BrushOf(bar.Tone, bar.Faint ? (byte)0x55 : (byte)0xCC), new Rect(bar.X, bar.Y, bar.Width, bar.Height));
        }

        foreach (var line in layout.Lines)
        {
            DrawLine(context, layout, line);
        }

        if (model.IsEmpty)
        {
            var empty = Text(AppLocalizer.Instance.Get("Analytics.Chart.NoData"), label, 12);
            context.DrawText(empty, new Point(
                (layout.PlotLeft + layout.PlotRight - empty.WidthIncludingTrailingWhitespace) / 2,
                (layout.PlotTop + layout.PlotBottom - empty.Height) / 2));
        }

        if (HoverTime is { } time && layout.HoverAtTime(time) is { } hover)
        {
            DrawHover(context, layout, hover);
        }
    }

    private static void DrawLine(DrawingContext context, ChartLayout layout, ChartLine line)
    {
        var brush = BrushOf(line.Tone, line.Faint ? (byte)0x80 : (byte)0xFF);
        var stroke = new Pen(brush, line.Faint ? 1.5 : 2, line.Faint ? new DashStyle([3, 2], 0) : null, lineJoin: PenLineJoin.Round);
        var fill = BrushOf(line.Tone, 0x30);

        foreach (var segment in line.Segments)
        {
            if (segment.Count == 1)
            {
                context.DrawEllipse(brush, null, new Point(segment[0].X, segment[0].Y), 2.5, 2.5);
                continue;
            }

            if (line.Fill)
            {
                var area = new StreamGeometry();

                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(segment[0].X, layout.FillBaseline), isFilled: true);

                    foreach (var point in segment)
                    {
                        ctx.LineTo(new Point(point.X, point.Y));
                    }

                    ctx.LineTo(new Point(segment[^1].X, layout.FillBaseline));
                    ctx.EndFigure(isClosed: true);
                }

                context.DrawGeometry(fill, null, area);
            }

            var path = new StreamGeometry();

            using (var ctx = path.Open())
            {
                ctx.BeginFigure(new Point(segment[0].X, segment[0].Y), isFilled: false);

                foreach (var point in segment.Skip(1))
                {
                    ctx.LineTo(new Point(point.X, point.Y));
                }

                ctx.EndFigure(isClosed: false);
            }

            context.DrawGeometry(null, stroke, path);
        }
    }

    private void DrawHover(DrawingContext context, ChartLayout layout, ChartHover hover)
    {
        var x = Math.Round(hover.X) + 0.5;
        context.DrawLine(new Pen(ForegroundWith(0x66), 1), new Point(x, layout.PlotTop), new Point(x, layout.PlotBottom));

        foreach (var row in hover.Rows)
        {
            if (row.Y is { } y)
            {
                context.DrawEllipse(BrushOf(row.Tone, row.Faint ? (byte)0x99 : (byte)0xFF), new Pen(TooltipBackground, 1.5), new Point(hover.X, y), 4, 4);
            }
        }

        // İpucu kutusu: imlecin sağında, sığmazsa solunda.
        var title = Text(hover.Title, TooltipText, ChartLayout.FontSize + 1, FontWeight.SemiBold);
        var rows = hover.Rows.Select(r => (Row: r, Text: Text(r.Text, r.IsDetail ? TooltipDetail : TooltipText))).ToList();

        const double padding = 8;
        const double dot = 12;
        var width = Math.Max(title.WidthIncludingTrailingWhitespace, rows.Count == 0 ? 0 : rows.Max(r => r.Text.WidthIncludingTrailingWhitespace + dot)) + 2 * padding;
        var height = title.Height + rows.Sum(r => r.Text.Height + 2) + 2 * padding + 2;

        var left = hover.X + 12 + width <= Bounds.Width ? hover.X + 12 : Math.Max(0, hover.X - 12 - width);
        var top = layout.PlotTop;

        context.DrawRectangle(TooltipBackground, new Pen(TooltipBorder, 1), new Rect(left, top, width, height), 6, 6);
        context.DrawText(title, new Point(left + padding, top + padding));

        var rowTop = top + padding + title.Height + 4;

        foreach (var (row, text) in rows)
        {
            if (!row.IsDetail)
            {
                context.DrawEllipse(BrushOf(row.Tone, row.Faint ? (byte)0x99 : (byte)0xFF), null, new Point(left + padding + 4, rowTop + text.Height / 2), 4, 4);
            }

            context.DrawText(text, new Point(left + padding + dot, rowTop));
            rowTop += text.Height + 2;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetCurrentValue(HoverTimeProperty, _layout?.HoverAt(e.GetPosition(this).X)?.Time);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetCurrentValue(HoverTimeProperty, null);
    }
}
