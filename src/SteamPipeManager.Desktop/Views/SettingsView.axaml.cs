using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
