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
    /// <summary>
    /// Tepsi simgesinin menüsünü baştan doldurur. Menü nesnesi hep aynı kalmalı: macOS'ta
    /// tepsi simgesine yeni bir <see cref="NativeMenu"/> vermek "The menu being updated
    /// does not match" hatasıyla uygulamayı düşürüyor (CI'da görüldü).
    /// </summary>
    public static void Fill(NativeMenu menu, IEnumerable<WidgetMenuEntry> entries)
    {
        menu.Items.Clear();

        foreach (var item in ToNativeItems(entries))
        {
            menu.Items.Add(item);
        }
    }

    /// <summary>Menünün içeriğini özetler; değişmediyse yeniden doldurmaya gerek yok.</summary>
    public static string Signature(IEnumerable<WidgetMenuEntry> entries) =>
        string.Join("|", entries.Select(e =>
            $"{e.Header}:{e.IsChecked}" + (e.Children is { } children ? "[" + Signature(children) + "]" : "")));

    private static NativeMenu ToNativeMenu(IEnumerable<WidgetMenuEntry> entries)
    {
        var menu = new NativeMenu();

        foreach (var item in ToNativeItems(entries))
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private static IEnumerable<NativeMenuItemBase> ToNativeItems(IEnumerable<WidgetMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                yield return new NativeMenuItemSeparator();
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

            yield return item;
        }
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
