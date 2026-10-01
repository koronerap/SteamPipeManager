using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class AppPickerView : UserControl
{
    public AppPickerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
