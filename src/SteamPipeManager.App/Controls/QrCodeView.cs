using System.Windows;
using System.Windows.Media;

namespace SteamPipeManager.App.Controls;

/// <summary>
/// QR kodu: beyaz zemin, dört karelik kenar boşluğu, tam piksele oturan kareler (bulanık
/// kenar taramayı zorlaştırıyor). Avalonia'daki eşi aynı yapıda.
/// </summary>
public sealed class QrCodeView : FrameworkElement
{
    public static readonly DependencyProperty ModulesProperty = DependencyProperty.Register(
        nameof(Modules), typeof(bool[,]), typeof(QrCodeView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int QuietZone = 4;

    public QrCodeView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public bool[,]? Modules
    {
        get => (bool[,]?)GetValue(ModulesProperty);
        set => SetValue(ModulesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Modules is not { } modules)
        {
            return;
        }

        var count = modules.GetLength(0) + 2 * QuietZone;
        var cell = Math.Max(1, Math.Floor(Math.Min(ActualWidth, ActualHeight) / count));
        var size = cell * count;
        var left = Math.Floor((ActualWidth - size) / 2);
        var top = Math.Floor((ActualHeight - size) / 2);

        dc.DrawRectangle(Brushes.White, null, new Rect(left, top, size, size));

        for (var y = 0; y < modules.GetLength(0); y++)
        {
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                if (modules[y, x])
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(left + (x + QuietZone) * cell, top + (y + QuietZone) * cell, cell, cell));
                }
            }
        }
    }
}
