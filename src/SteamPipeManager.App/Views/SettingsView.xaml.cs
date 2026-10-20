using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is SettingsViewModel current)
            {
                current.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnFinancialKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.FinancialKeyInput = FinancialKeyInput.Password;
        }
    }

    /// <summary>Anahtar kaydedilince ya da sayfa yeniden açılınca kutu da boşalsın.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FinancialKeyInput) &&
            sender is SettingsViewModel { FinancialKeyInput.Length: 0 } &&
            FinancialKeyInput.Password.Length > 0)
        {
            FinancialKeyInput.Clear();
        }
    }
}
