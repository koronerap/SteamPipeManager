using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Telefona aktarım metni panoya: kamera tarayamazsa telefonda yapıştırmak için.</summary>
    private async void CopyPairingText(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { PairingText: { } text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
