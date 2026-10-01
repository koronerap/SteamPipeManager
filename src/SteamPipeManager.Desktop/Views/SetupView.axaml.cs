using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class SetupView : UserControl
{
    public SetupView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
