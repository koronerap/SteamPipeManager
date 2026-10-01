using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace SteamPipeManager.Desktop.Controls;

/// <summary>
/// Simge ve metin yan yana; düğme içeriği olarak. WPF-UI düğmelerindeki
/// <c>Icon</c> + <c>Content</c> ikilisinin karşılığı.
/// </summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<IconLabel, Symbol>(nameof(Symbol));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    private readonly SymbolIcon _icon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Children.Add(_icon);
        Children.Add(_text);
    }

    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Ekran okuyucular ve otomasyon düğmenin adını içeriğinden alıyor; sınıf adı
    /// yerine metin okunsun.
    /// </summary>
    public override string ToString() => Text ?? "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SymbolProperty)
        {
            _icon.Symbol = Symbol;
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
            _text.IsVisible = !string.IsNullOrEmpty(Text);
        }
    }
}
