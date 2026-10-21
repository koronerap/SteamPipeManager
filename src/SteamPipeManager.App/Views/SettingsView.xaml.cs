using System.Windows;
using System.Windows.Controls;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Telefona aktarım metni panoya: kamera tarayamazsa telefonda yapıştırmak için.</summary>
    private void CopyPairingText(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { PairingText: { } text })
        {
            Clipboard.SetText(text);
        }
    }
}
