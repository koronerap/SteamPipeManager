using Avalonia.Controls;

namespace SteamPipeManager.Desktop.Widget;

/// <summary>
/// Widget menüsünün tek tanımı. İki yerde gösteriliyor: tepsi simgesi (sistem menüsü)
/// ve şeridin sağ tık menüsü. Tepsi simgesi olmayan masaüstlerinde (eklentisiz GNOME)
/// her şeye şeritten ulaşılabilsin diye ikisi aynı.
/// </summary>
internal sealed record WidgetMenuEntry(
    string Header,
    Action? Run = null,
    bool? IsChecked = null,
    IReadOnlyList<WidgetMenuEntry>? Children = null)
{
    public static readonly WidgetMenuEntry Separator = new("-");

    public bool IsSeparator => ReferenceEquals(this, Separator);
}

internal static class WidgetMenu
{
    /// <summary>Tepsi simgesinin menüsü.</summary>
    public static NativeMenu ToNativeMenu(IEnumerable<WidgetMenuEntry> entries)
    {
        var menu = new NativeMenu();

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(entry.Header);

            if (entry.IsChecked is { } isChecked)
            {
                item.ToggleType = NativeMenuItemToggleType.CheckBox;
                item.IsChecked = isChecked;
            }

            if (entry.Children is { Count: > 0 } children)
            {
                item.Menu = ToNativeMenu(children);
            }
            else if (entry.Run is { } run)
            {
                item.Click += (_, _) => run();
            }

            menu.Add(item);
        }

        return menu;
    }

    /// <summary>Şeridin sağ tık menüsü.</summary>
    public static List<Control> ToContextItems(IEnumerable<WidgetMenuEntry> entries)
    {
        var items = new List<Control>();

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = entry.Header };

            if (entry.IsChecked is { } isChecked)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = isChecked;
            }

            if (entry.Children is { Count: > 0 } children)
            {
                item.ItemsSource = ToContextItems(children);
            }
            else if (entry.Run is { } run)
            {
                item.Click += (_, _) => run();
            }

            items.Add(item);
        }

        return items;
    }
}
