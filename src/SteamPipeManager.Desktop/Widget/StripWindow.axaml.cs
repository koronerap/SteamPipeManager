using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Her zaman üstte duran şerit. Konumu sağ alt köşesiyle tutuluyor: içerik uzayınca
/// şerit sola doğru büyüyor, ekranın sağ altında (görev çubuğunun üstünde) yerinde kalıyor.
/// Sürüklenip bırakılınca alttaki kenara yakınsa görev çubuğunun hemen üstüne yaslanıyor.
/// </summary>
public sealed partial class StripWindow : Window
{
    /// <summary>Ekran kenarından ve görev çubuğundan uzaklık (ölçeklenmemiş piksel).</summary>
    private const int Margin = 12;

    /// <summary>Alt kenara bu kadar yakın bırakılırsa görev çubuğunun üstüne yaslanıyor.</summary>
    private const int SnapDistance = 32;

    private readonly DispatcherTimer _settle;
    private PixelPoint? _anchor;

    /// <summary>
    /// Koddan verilen son konum. Konum olayı bazı platformlarda ayarlandıktan sonra,
    /// ayrı bir turda geliyor; kullanıcının taşıması sanılıp varsayılan konum
    /// "kullanıcının seçimi" olarak kaydedilmesin.
    /// </summary>
    private PixelPoint? _placed;

    public StripWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var body = this.FindControl<Border>("Body")!;
        var details = this.FindControl<Button>("DetailsButton")!;

        details.Click += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        body.DoubleTapped += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        body.PointerPressed += OnPointerPressed;

        // Sürükleme bitince (konum bir süre değişmeyince) yaslama ve kayıt.
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            SettleAfterMove();
        };

        PositionChanged += (_, e) =>
        {
            if (e.Point != _placed)
            {
                _settle.Stop();
                _settle.Start();
            }
        };

        Opened += (_, _) => Place();
    }

    /// <summary>İçerik değişip boyut değişince sağ alt köşe yerinde kalsın.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ClientSizeProperty)
        {
            Place();
        }
    }

    public event EventHandler? DetailsRequested;

    /// <summary>Kullanıcı şeridi taşıdı; yeni sağ alt köşe (fiziksel piksel).</summary>
    public event EventHandler<PixelPoint>? AnchorChanged;

    /// <summary>Kayıtlı sağ alt köşe; yoksa birincil ekranın sağ altı.</summary>
    public void SetAnchor(int? right, int? bottom)
    {
        _anchor = right is { } r && bottom is { } b ? new PixelPoint(r, b) : null;
        Place();
    }

    internal void SetMenu(IEnumerable<WidgetMenuEntry> entries) =>
        ContextMenu = new ContextMenu { ItemsSource = WidgetMenu.ToContextItems(entries) };

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private PixelSize PhysicalSize
    {
        get
        {
            var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
            return new PixelSize((int)Math.Ceiling(Bounds.Width * scaling), (int)Math.Ceiling(Bounds.Height * scaling));
        }
    }

    private void Place()
    {
        if (!IsVisible || Bounds.Width <= 0)
        {
            return;
        }

        var size = PhysicalSize;
        var anchor = _anchor ?? DefaultAnchor();

        // Kayıtlı konum artık bir ekranda değilse (monitör çıkarıldı) varsayılana dön.
        if (Screens.ScreenFromPoint(new PixelPoint(anchor.X - 1, anchor.Y - 1)) is null)
        {
            anchor = DefaultAnchor();
        }

        _placed = new PixelPoint(anchor.X - size.Width, anchor.Y - size.Height);
        Position = _placed.Value;
    }

    private PixelPoint DefaultAnchor()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();

        if (screen is null)
        {
            return new PixelPoint(800, 600);
        }

        var area = screen.WorkingArea;
        var margin = (int)(Margin * screen.Scaling);
        return new PixelPoint(area.Right - margin, area.Bottom - margin);
    }

    private void SettleAfterMove()
    {
        var size = PhysicalSize;
        var anchor = new PixelPoint(Position.X + size.Width, Position.Y + size.Height);

        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            var margin = (int)(Margin * screen.Scaling);

            // Alt kenara yakınsa görev çubuğunun hemen üstüne.
            if (Math.Abs(area.Bottom - anchor.Y) < SnapDistance * screen.Scaling)
            {
                anchor = new PixelPoint(anchor.X, area.Bottom - margin);
            }

            // Ekranın dışına taşmasın.
            anchor = new PixelPoint(
                Math.Clamp(anchor.X, area.X + size.Width, area.Right),
                Math.Clamp(anchor.Y, area.Y + size.Height, area.Bottom));
        }

        _anchor = anchor;
        Place();
        AnchorChanged?.Invoke(this, anchor);
    }
}
