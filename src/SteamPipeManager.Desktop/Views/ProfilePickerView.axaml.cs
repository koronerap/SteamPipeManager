using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class ProfilePickerView : UserControl
{
    public ProfilePickerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
