using System.Windows;
using System.Windows.Controls;
using SteamPipeManager.App.ViewModels;

namespace SteamPipeManager.App.Views;

public partial class ProfilePickerView : UserControl
{
    public ProfilePickerView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ProfilePickerViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is ProfilePickerViewModel current)
            {
                current.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    /// <summary>
    /// Düzenlenen profil değişince (ya da form kapanınca) secret kutusu boşaltılıyor:
    /// önceki profile yazılan secret bir sonrakinin formunda görünmesin.
    /// </summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilePickerViewModel.Editing))
        {
            EditClientSecretInput.Clear();
        }
    }

    private void OnEditClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilePickerViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.EditClientSecret = box.Password;
        }
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
