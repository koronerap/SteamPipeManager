using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Şerit; varsayılan olarak her zaman üstte. Konumu sağ alt köşesiyle tutuluyor: içerik
/// uzayınca şerit sola (dikeyde yukarı) doğru büyüyor, ekranın sağ altında (görev çubuğunun
/// üstünde) yerinde kalıyor. Sürüklenip bırakılınca alttaki kenara yakınsa görev çubuğunun
/// hemen üstüne yaslanıyor.
/// </summary>
public sealed partial class StripWindow : Window
{
    /// <summary>Ekran kenarından ve görev çubuğundan uzaklık (ölçeklenmemiş piksel).</summary>
    private const int Margin = 12;

    /// <summary>Alt kenara bu kadar yakın bırakılırsa görev çubuğunun üstüne yaslanıyor.</summary>
    private const int SnapDistance = 32;

    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private readonly DispatcherTimer _settle;
    private readonly LayoutTransformControl _scaler;
    private readonly Border _backdrop;
    private readonly Border _body;
    private readonly Control _logo;
    private readonly Control _heading;
    private readonly Control _itemsArea;
    private readonly Control _details;
    private readonly ItemsControl _items;
    private PixelPoint? _anchor;
    private double _scale = 1;
    private WidgetOrientation? _orientation;
    private WidgetTheme _theme = WidgetTheme.Dark;
    private bool _locked;

    /// <summary>
    /// Koddan verilen son konum. Konum olayı bazı platformlarda ayarlandıktan sonra,
    /// ayrı bir turda geliyor; kullanıcının taşıması sanılıp varsayılan konum
    /// "kullanıcının seçimi" olarak kaydedilmesin.
    /// </summary>
    private PixelPoint? _placed;

    public StripWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _scaler = this.FindControl<LayoutTransformControl>("Scaler")!;
        _backdrop = this.FindControl<Border>("Backdrop")!;
        _body = this.FindControl<Border>("Body")!;
        _logo = this.FindControl<Control>("Logo")!;
        _heading = this.FindControl<Control>("Heading")!;
        _itemsArea = this.FindControl<Control>("ItemsArea")!;
        _items = this.FindControl<ItemsControl>("ItemsList")!;

        var details = this.FindControl<Button>("DetailsButton")!;
        _details = details;

        details.Click += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        _body.DoubleTapped += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        _body.PointerPressed += OnPointerPressed;

        // "Sistemle aynı" tema: işletim sisteminin teması değişince şerit de değişsin.
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => RequestedThemeVariant = ResolveTheme());
        }

        ApplyOrientation(WidgetOrientation.Horizontal, showTitles: true);

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

    /// <summary>Görünüm ayarları: boyut, yerleşim, saydamlık, tema, üstte durma ve kilit.</summary>
    public void ApplyLook(WidgetSettings settings)
    {
        var scale = WidgetLook.Scale(settings.TextSize);

        if (scale != _scale)
        {
            _scale = scale;
            _scaler.LayoutTransform = new ScaleTransform(scale, scale);
        }

        _backdrop.Opacity = WidgetLook.BackgroundOpacity(settings.Transparency);
        Topmost = settings.AlwaysOnTop;

        _theme = settings.Theme;
        RequestedThemeVariant = ResolveTheme();

        _locked = settings.LockPosition;
        _body.Cursor = _locked ? Cursor.Default : MoveCursor;

        ApplyOrientation(settings.Orientation, settings.ShowTitles);
    }

    private ThemeVariant ResolveTheme() => _theme switch
    {
        WidgetTheme.Light => ThemeVariant.Light,
        WidgetTheme.System when Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light => ThemeVariant.Light,
        _ => ThemeVariant.Dark,
    };

    /// <summary>
    /// Yatay: simge, oyunlar ve düğme tek satırda. Dikey: üstte simge, ad ve düğme; altında
    /// oyunlar alt alta, sayıları sütun sütun hizalı.
    /// </summary>
    private void ApplyOrientation(WidgetOrientation orientation, bool showTitles)
    {
        var vertical = orientation == WidgetOrientation.Vertical;

        if (orientation != _orientation)
        {
            _orientation = orientation;
            _items.ItemsPanel = (ItemsPanelTemplate)this.FindResource(vertical ? "ColumnPanel" : "RowPanel")!;
            _items.ItemTemplate = (IDataTemplate)this.FindResource(vertical ? "ColumnItem" : "RowItem")!;

            Put(_logo, row: 0, column: 0);
            Put(_heading, row: 0, column: 1);
            Put(_details, row: 0, column: 2);
            Put(_itemsArea, row: vertical ? 1 : 0, column: vertical ? 0 : 1, span: vertical ? 3 : 1);

            _heading.IsVisible = vertical;
            _details.HorizontalAlignment = vertical ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _details.Margin = vertical ? new Thickness(14, 0, 0, 0) : default;
        }

        // Dikeyde sayı sütunları soldan boşlukla başlıyor; ad gizliyse o boşluk kenara yapışmasın.
        _itemsArea.Margin = vertical ? new Thickness(showTitles ? 0 : -14, 6, 0, 2) : new Thickness(14, 0);

        // Paylaşılan sütun genişlikleri kendiliğinden küçülmüyor: ad ya da bir sayı gizlenince
        // sütun eski genişliğinde boş kalıyordu. Her değişiklikte sıfırdan ölçülsün.
        if (vertical)
        {
            Grid.SetIsSharedSizeScope(_items, false);
            Grid.SetIsSharedSizeScope(_items, true);
        }

        static void Put(Control control, int row, int column, int span = 1)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, span);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>
    /// Pencerenin boyutu. <see cref="Visual.Bounds"/> değil: açılırken yerleşim turundan önce
    /// hâlâ varsayılan pencere boyutunu gösteriyor ve şerit ekranın ortasında kalıyordu.
    /// </summary>
    private PixelSize PhysicalSize
    {
        get
        {
            var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
            return new PixelSize((int)Math.Ceiling(ClientSize.Width * scaling), (int)Math.Ceiling(ClientSize.Height * scaling));
        }
    }

    private void Place()
    {
        if (!IsVisible || ClientSize.Width <= 0)
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
