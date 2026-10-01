using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Views;

public sealed partial class LoginOverlay : UserControl
{
    private LoginViewModel? _viewModel;

    public LoginOverlay()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach(DataContext as LoginViewModel);
    }

    /// <summary>
    /// Giriş penceresi kapanınca parola kutusu boşaltılıyor. ViewModel parolayı
    /// unutuyor ama kutu kendi metnini tutardı; bir sonraki açılışta görünmemeli.
    /// </summary>
    private void Attach(LoginViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.IsOpen) && _viewModel is { IsOpen: false })
        {
            foreach (var box in this.GetLogicalDescendants().OfType<TextBox>().Where(t => t.PasswordChar != default))
            {
                box.Text = "";
            }
        }
    }
}
