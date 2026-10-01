using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class EpicWorkspaceView : UserControl
{
    public EpicWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
