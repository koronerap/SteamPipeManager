using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class AnalyticsView : UserControl
{
    public AnalyticsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
