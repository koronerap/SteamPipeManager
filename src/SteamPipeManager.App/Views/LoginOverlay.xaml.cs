using System.Windows;
using System.Windows.Controls;
using SteamPipeManager.App.ViewModels;

namespace SteamPipeManager.App.Views;

public partial class LoginOverlay : UserControl
{
    public LoginOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <c>PasswordBox.Password</c> güvenlik gereği bağlanabilir bir özellik değil,
    /// bu yüzden değer buradan ViewModel'e aktarılıyor.
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Password = box.Password;
        }
    }
}
