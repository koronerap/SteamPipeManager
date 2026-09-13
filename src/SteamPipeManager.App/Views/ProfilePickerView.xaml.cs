using System.Windows;
using System.Windows.Controls;
using SteamPipeManager.App.ViewModels;

namespace SteamPipeManager.App.Views;

public partial class ProfilePickerView : UserControl
{
    public ProfilePickerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <c>PasswordBox.Password</c> güvenlik gereği bağlanabilir bir özellik değil —
    /// bağlama yazıldığında sessizce hiçbir şey olmuyor. Değer buradan ViewModel'e
    /// aktarılıyor; giriş ekranında da aynı kalıp kullanılıyor.
    /// </summary>
    private void OnNewClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilePickerViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.NewClientSecret = box.Password;
        }
    }
}
