using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SteamPipeManager.Desktop.Controls;

/// <summary>
/// QR kodu: beyaz zemin, dört karelik kenar boşluğu, tam piksele oturan kareler (bulanık
/// kenar taramayı zorlaştırıyor). WPF'teki eşi aynı yapıda.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<bool[,]?> ModulesProperty =
        AvaloniaProperty.Register<QrCodeView, bool[,]?>(nameof(Modules));

    private const int QuietZone = 4;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(ModulesProperty);
    }

    public bool[,]? Modules
    {
        get => GetValue(ModulesProperty);
        set => SetValue(ModulesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Modules is not { } modules)
        {
            return;
        }

        var count = modules.GetLength(0) + 2 * QuietZone;
        var cell = Math.Max(1, Math.Floor(Math.Min(Bounds.Width, Bounds.Height) / count));
        var size = cell * count;
        var left = Math.Floor((Bounds.Width - size) / 2);
        var top = Math.Floor((Bounds.Height - size) / 2);

        context.FillRectangle(Brushes.White, new Rect(left, top, size, size));

        for (var y = 0; y < modules.GetLength(0); y++)
        {
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                if (modules[y, x])
                {
                    context.FillRectangle(Brushes.Black, new Rect(left + (x + QuietZone) * cell, top + (y + QuietZone) * cell, cell, cell));
                }
            }
        }
    }
}
